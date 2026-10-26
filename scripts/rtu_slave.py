"""Modbus RTU 从站模拟器 + 汇川 H5U 的 ST 逻辑模拟。

用途：在没有真 PLC 的机器上验证 VisionStation 的 ModbusRtuPlc 路径。
拓扑：COM3(ModbusRtuPlc) <-> com0com <-> COM4(本脚本)

支持功能码：03 读保持寄存器 / 06 写单寄存器 / 01 读线圈 / 05 写单线圈 / 10 写多寄存器
寄存器映射（偏移 = 汇川 D 号，与 ModbusAddressParser 一致）：
  D100 触发位  D101 结果位  D102 心跳  D103 OK计数  D104 NG计数  D105 在线  D106 触发计数

与 docs/plc/触发与回写.st 对齐的从站行为：
  · 每隔 TRIGGER_PERIOD 秒把 D100 置 1（模拟光电传感器到位）
  · 主机清 D100 = 握手完成
  · 主机写 D101(1=OK/2=NG) → 计入 D103/D104，随后清 D101
  · 心跳 D102 两个周期不动 → D105 = 0（模拟看门狗判离线）

用法：python rtu_slave.py [PORT] [BAUD] [--no-trigger]
退出：Ctrl+C（会打印统计）
"""
import sys
import threading
import time

import serial

PORT = "COM4"
BAUD = 9600
STATION = 1
TRIGGER_PERIOD = 2.0
HEARTBEAT_TIMEOUT = 2.0

REG_TRIGGER = 100      # D100
REG_RESULT = 101       # D101
REG_HEARTBEAT = 102    # D102
REG_OK = 103           # D103
REG_NG = 104           # D104
REG_ONLINE = 105       # D105
REG_TRIG_COUNT = 106   # D106

regs = [0] * 200
coils = [0] * 64
discrete = [0] * 64
lock = threading.Lock()
stop = threading.Event()
stats = {"frames": 0, "crc_err": 0, "triggers": 0, "cleared": 0, "ok": 0, "ng": 0, "hb": 0, "hb_lost": 0}
last_hb_value = 0
last_hb_time = time.time()

NAMES = {
    REG_TRIGGER: "D100 触发位",
    REG_RESULT: "D101 结果位",
    REG_HEARTBEAT: "D102 心跳",
    REG_OK: "D103 OK计数",
    REG_NG: "D104 NG计数",
    REG_ONLINE: "D105 在线",
    REG_TRIG_COUNT: "D106 触发计数",
}


def log(msg):
    now = time.time()
    print(f"[{time.strftime('%H:%M:%S', time.localtime(now))}.{int(now * 1000) % 1000:03d}] {msg}", flush=True)


def crc16(data: bytes) -> int:
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            if crc & 1:
                crc = (crc >> 1) ^ 0xA001
            else:
                crc >>= 1
    return crc


def with_crc(payload: bytes) -> bytes:
    c = crc16(payload)
    return payload + bytes([c & 0xFF, (c >> 8) & 0xFF])


def label(offset: int) -> str:
    return NAMES.get(offset, f"D{offset}")


def read_frame(ser: serial.Serial):
    """按功能码精确读取一帧。返回 (addr, func, rest) 或 None。"""
    hdr = ser.read(2)
    if len(hdr) < 2:
        return None
    addr, func = hdr[0], hdr[1]
    if func == 0x10:
        head = ser.read(5)
        if len(head) < 5:
            return None
        return addr, func, head + ser.read(head[4] + 2)
    rest = ser.read(6)
    if len(rest) < 6:
        return None
    return addr, func, rest


def st_worker(allow_trigger=True):
    """模拟 PLC 的 ST 程序：周期触发、消费结果、心跳看门狗。"""
    global last_hb_time, last_hb_value
    next_trigger = time.time() + 1.0
    while not stop.is_set():
        time.sleep(0.05)
        now = time.time()
        with lock:
            regs[REG_ONLINE] = 1 if (now - last_hb_time) < HEARTBEAT_TIMEOUT else 0
            if (now - last_hb_time) >= HEARTBEAT_TIMEOUT and last_hb_time != 0:
                if stats["hb_lost"] == 0:
                    log("ST: 心跳超时 → D105=0（主机失联）")
                stats["hb_lost"] += 1

            result = regs[REG_RESULT]
            if result != 0:
                if result == 1:
                    stats["ok"] += 1
                    regs[REG_OK] += 1
                    log(f"ST: 读到 D101=1 → OK 计数 D103={regs[REG_OK]}")
                elif result == 2:
                    stats["ng"] += 1
                    regs[REG_NG] += 1
                    log(f"ST: 读到 D101=2 → NG 计数 D104={regs[REG_NG]}")
                else:
                    log(f"ST: D101={result} 非法结果码，忽略")
                regs[REG_RESULT] = 0

            if allow_trigger and regs[REG_TRIGGER] == 0 and now >= next_trigger:
                regs[REG_TRIGGER] = 1
                regs[REG_TRIG_COUNT] += 1
                stats["triggers"] += 1
                log(f"ST: D100=1 触发第 {regs[REG_TRIG_COUNT]} 件（等待主机清位）")
                next_trigger = now + TRIGGER_PERIOD


def handle(addr: int, func: int, rest: bytes):
    """返回响应帧（bytes）或 None（不该响应）。"""
    global last_hb_time, last_hb_value

    body = bytes([addr, func]) + rest
    expect = crc16(body[:-2])
    got_crc = body[-2] | (body[-1] << 8)
    if expect != got_crc:
        stats["crc_err"] += 1
        log(f"!! CRC 错：期望 {expect:04X} 实得 {got_crc:04X}  {body.hex(' ')}")
        return None
    if addr != STATION:
        return None   # 不是我的站号，静默

    with lock:
        if func == 0x03:
            start = (rest[0] << 8) | rest[1]
            count = (rest[2] << 8) | rest[3]
            data = b""
            for i in range(count):
                v = regs[start + i] if start + i < len(regs) else 0
                data += bytes([(v >> 8) & 0xFF, v & 0xFF])
            log(f"RX 03 读 {label(start)} x{count} → {[regs[start + i] for i in range(min(count, 4))]}")
            return with_crc(bytes([STATION, 0x03, len(data)]) + data)

        if func == 0x06:
            reg = (rest[0] << 8) | rest[1]
            val = (rest[2] << 8) | rest[3]
            old = regs[reg]
            regs[reg] = val
            if reg == REG_TRIGGER:
                if old != 0 and val == 0:
                    stats["cleared"] += 1
                    log(f"RX 06 CLEAR {label(reg)} = 0   <<< 握手完成（第 {stats['cleared']} 次清位）")
                else:
                    log(f"RX 06 {label(reg)} = {val}（原 {old}）")
            elif reg == REG_HEARTBEAT:
                global_last = last_hb_value
                stats["hb"] += 1
                last_hb_value = val
                last_hb_time = time.time()
                if stats["hb"] <= 3 or val == 0:
                    log(f"RX 06 {label(reg)} = {val}（原 {global_last}，第 {stats['hb']} 次心跳）")
            elif reg == REG_RESULT:
                log(f"RX 06 {label(reg)} = {val}   <<< 判定写回")
            else:
                log(f"RX 06 {label(reg)} = {val}（原 {old}）")
            return with_crc(body)

        if func == 0x01:
            start = (rest[0] << 8) | rest[1]
            count = (rest[2] << 8) | rest[3]
            byte_count = (count + 7) // 8
            data = bytearray(byte_count)
            for i in range(count):
                if start + i < len(coils) and coils[start + i]:
                    data[i // 8] |= 1 << (i % 8)
            log(f"RX 01 读线圈 M{start} x{count}")
            return with_crc(bytes([STATION, 0x01, byte_count]) + bytes(data))

        if func == 0x02:
            start = (rest[0] << 8) | rest[1]
            count = (rest[2] << 8) | rest[3]
            byte_count = (count + 7) // 8
            data = bytearray(byte_count)
            for i in range(count):
                if start + i < len(discrete) and discrete[start + i]:
                    data[i // 8] |= 1 << (i % 8)
            log(f"RX 02 读离散输入 X{start} x{count}")
            return with_crc(bytes([STATION, 0x02, byte_count]) + bytes(data))

        if func == 0x05:
            coil = (rest[0] << 8) | rest[1]
            val = 0xFF00 if (rest[2] == 0xFF) else 0x0000
            if coil < len(coils):
                coils[coil] = 1 if val else 0
            log(f"RX 05 写线圈 M{coil} = {bool(val)}")
            return with_crc(body)

        if func == 0x10:
            start = (rest[0] << 8) | rest[1]
            count = (rest[2] << 8) | rest[3]
            data = rest[5:-2]
            for i in range(count):
                regs[start + i] = (data[i * 2] << 8) | data[i * 2 + 1]
            log(f"RX 10 写 {label(start)} x{count} = {[regs[start + i] for i in range(min(count, 4))]}")
            return with_crc(bytes([STATION, 0x10]) + rest[:4])

        # 不支持的功能码 → 异常响应 01
        log(f"RX {func:02X} 不支持的功能码 → 异常响应")
        return with_crc(bytes([STATION, func | 0x80, 0x01]))


def main():
    global PORT, BAUD, stop
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    no_trigger = "--no-trigger" in sys.argv
    if len(args) >= 1:
        PORT = args[0]
    if len(args) >= 2:
        BAUD = int(args[1])

    ser = serial.Serial(PORT, BAUD, timeout=0.2, bytesize=8, parity="N", stopbits=1)
    log(f"RTU 从站启动：{PORT} {BAUD}-8-N-1 站号{STATION}  触发周期 {TRIGGER_PERIOD}s"
        + ("（被动模式，不主动触发）" if no_trigger else ""))
    threading.Thread(target=st_worker, args=(not no_trigger,), daemon=True).start()

    try:
        while not stop.is_set():
            frame = read_frame(ser)
            if frame is None:
                continue
            addr, func, rest = frame
            stats["frames"] += 1
            resp = handle(addr, func, rest)
            if resp:
                ser.write(resp)
                ser.flush()
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        ser.close()
        with lock:
            log(f"统计：帧 {stats['frames']}  CRC错 {stats['crc_err']}  触发 {stats['triggers']}  "
                f"清位 {stats['cleared']}  OK {stats['ok']}  NG {stats['ng']}  心跳 {stats['hb']}")
            log(f"最终寄存器：D100={regs[100]} D101={regs[101]} D102={regs[102]} "
                f"D103={regs[103]} D104={regs[104]} D105={regs[105]} D106={regs[106]}")


if __name__ == "__main__":
    main()
