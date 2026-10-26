// ModbusRtuPlc 验证探针 —— 需要一对虚拟串口（com0com 的 COM3 <-> COM4）。
//
// 用法：
//   终端 1：python scripts/rtu_slave.py COM4 9600 --no-trigger
//   终端 2：dotnet run --project tools/RtuProbe -- COM3 9600
//
// 默认端口 COM3/9600。这是手动工具，**不在 ci.cmd 的构建范围内**（无串口的机器上跑不了）。

using System.Diagnostics;
using VisionStation.Devices.Plc;

var port = args.Length > 0 ? args[0] : "COM3";
var baud = args.Length > 1 && int.TryParse(args[1], out var parsedBaud) ? parsedBaud : 9600;

var pass = 0;
var fail = 0;
var failures = new List<string>();

void Check(string name, bool ok, string detail = "")
{
    if (ok)
    {
        pass++;
        Console.WriteLine("  [PASS] " + name + (detail.Length > 0 ? "  " + detail : ""));
    }
    else
    {
        fail++;
        failures.Add(name + (detail.Length > 0 ? "  " + detail : ""));
        Console.WriteLine("  [FAIL] " + name + "  " + detail);
    }
}

Console.WriteLine("=== ModbusRtuPlc 验证：COM3 <-> COM4 虚拟串口对，COM4 侧是 Python RTU 从站 ===\n");

Console.WriteLine("1. 构造 / 参数文案");
var plc = new ModbusRtuPlc(port, baud, 1, 8, SerialParity.None, SerialStopBits.One, 500);
Check("Description 与参数一致", plc.Description == $"{port} {baud}-8-N-1 站号1", plc.Description);
Check("初始 IsConnected = false", !plc.IsConnected);

Console.WriteLine("\n2. 错误串口（COM99）不抛异常");
var bad = new ModbusRtuPlc("COM99", 9600, 1, receiveTimeoutMs: 300);
var threw = false;
var badOk = false;
try { badOk = bad.Connect(); }
catch (Exception ex) { threw = true; Console.WriteLine("    抛出: " + ex.Message); }
Check("Connect 不抛异常", !threw);
Check("Connect 返回 false", !badOk);
Check("LastError 带串口参数原文", bad.LastError is not null && bad.LastError.Contains("COM99"), bad.LastError ?? "(null)");
bad.Dispose();

Console.WriteLine("\n3. 正常连接 COM3");
var ok = plc.Connect();
Check("Connect 成功", ok);
Check("IsConnected = true", plc.IsConnected);
Check("LastError 清空", plc.LastError is null, plc.LastError ?? "(null)");
if (!ok)
{
    Console.WriteLine("\n连接失败，后续用例跳过。");
    return 1;
}

Console.WriteLine("\n4. 串口独占（COM3 已被本实例占用）");
var second = new ModbusRtuPlc(port, baud, 1, receiveTimeoutMs: 300);
var secondOk = second.Connect();
Check("第二个实例打不开同一个口", !secondOk, second.LastError ?? "");
second.Dispose();

Console.WriteLine("\n5. 保持寄存器读写 = 触发握手");
var d100 = ModbusAddressParser.Parse("D100");
plc.WriteShort(d100, 1);
var read1 = plc.ReadShort(d100);
Check("写 D100=1 后读回 1", read1 == 1, "值=" + read1);
plc.WriteShort(d100, 0);
var read0 = plc.ReadShort(d100);
Check("清 D100 后读回 0", read0 == 0, "值=" + read0);

Console.WriteLine("\n6. 结果写回 D101 -> 从站消费计数");
var d101 = ModbusAddressParser.Parse("D101");
var d104 = ModbusAddressParser.Parse("D104");
var ngBefore = plc.ReadShort(d104) ?? -1;
plc.WriteShort(d101, 2);
Thread.Sleep(600);
var ngAfter = plc.ReadShort(d104) ?? -1;
Check("写 D101=2 -> D104(NG) 计数 +1", ngAfter == ngBefore + 1, ngBefore + " -> " + ngAfter);

Console.WriteLine("\n7. 区域合法性把关");
var m10 = ModbusAddressParser.Parse("M10");
var badRead = plc.ReadShort(m10);
Check("ReadShort(M10) 返回 null", badRead is null);
Check("LastError 指出不是保持寄存器", plc.LastError is not null && plc.LastError.Contains("保持寄存器"), plc.LastError ?? "");
Check("WriteShort(M10) 返回 false", !plc.WriteShort(m10, 5));

Console.WriteLine("\n8. 线圈读写（M）");
Check("WriteBit(M10,true)", plc.WriteBit(m10, true));
Check("ReadBit(M10) = true", plc.ReadBit(m10) == true);
plc.WriteBit(m10, false);
Check("WriteBit(M10,false) 后读回 false", plc.ReadBit(m10) == false);

Console.WriteLine("\n9. 离散输入（X，功能码 02）");
var x5 = ModbusAddressParser.Parse("X5");
var xv = plc.ReadBit(x5);
Check("ReadBit(X5) 有响应（非 null）", xv is not null, "值=" + xv);
Check("LastError 为空", plc.LastError is null, plc.LastError ?? "");

Console.WriteLine("\n10. 并发读写（模拟轮询线程 + 回写线程共用实例）");
var d110 = ModbusAddressParser.Parse("D110");

// 先单线程跑一组做基准：串口本身能不能扛 50 笔
plc.Connect();
var baseWatch = Stopwatch.StartNew();
var baseFail = 0;
for (var i = 0; i < 50; i++)
    if (plc.ReadShort(d100) is null) baseFail++;
Console.WriteLine("    基准·单线程 50 笔：" + baseWatch.ElapsedMilliseconds + "ms，失败 " + baseFail + " 笔，IsConnected=" + plc.IsConnected);

// 生产场景：只有两个线程共用实例（轮询 + 回写）
var sw2p = Stopwatch.StartNew();
var fail2 = 0;
var t1 = Task.Run(() => { for (var i = 0; i < 50; i++) if (plc.ReadShort(d100) is null) Interlocked.Increment(ref fail2); });
var t2 = Task.Run(() => { for (var i = 0; i < 50; i++) if (!plc.WriteShort(d110, (short)i)) Interlocked.Increment(ref fail2); });
Task.WaitAll(t1, t2);
Console.WriteLine("    生产场景·2 路并发 100 笔：" + sw2p.ElapsedMilliseconds + "ms，失败 " + fail2 + " 笔");
Check("2 路并发零失败", fail2 == 0, "失败 " + fail2 + " 笔");
Check("2 路并发耗时 < 2s（每笔 <20ms）", sw2p.ElapsedMilliseconds < 2000, sw2p.ElapsedMilliseconds + "ms");

// 极端压力：30 路抢同一把锁（串口 9600 物理上不可能并行，这里只验证不坏）
var concFail = 0;
var concurrentErrors = new List<string>();
var sw = Stopwatch.StartNew();
Parallel.For(0, 30, i =>
{
    try
    {
        var r = i % 3 == 0
            ? (plc.WriteShort(d110, (short)i) ? (short?)0 : null)
            : plc.ReadShort(d110);
        if (r is null) Interlocked.Increment(ref concFail);
        if (plc.ReadShort(d100) is null) Interlocked.Increment(ref concFail);
    }
    catch (Exception ex)
    {
        lock (concurrentErrors) concurrentErrors.Add(ex.GetType().Name + ": " + ex.Message);
    }
});
Console.WriteLine("    极端压力·30 路（约 50 笔）：" + sw.ElapsedMilliseconds + "ms，失败 " + concFail + " 笔");
Check("极端压力无异常", concurrentErrors.Count == 0, string.Join("; ", concurrentErrors.Take(3)));
Check("单线程基准零失败", baseFail == 0, "失败 " + baseFail + " 笔");
Check("极端压力零失败", concFail == 0, "失败 " + concFail + " 笔");
Check("并发期间 IsConnected 保持 true", plc.IsConnected);
if (!plc.IsConnected)
{
    Console.WriteLine("    并发后 LastError: " + (plc.LastError ?? "(null)"));
    plc.Connect();
}

Console.WriteLine("\n11. 错误站号（5）-> 从站静默 -> 超时");
plc.Disconnect();
var wrong = new ModbusRtuPlc(port, baud, 5, receiveTimeoutMs: 300);
Check("串口仍能打开", wrong.Connect());
var sw2 = Stopwatch.StartNew();
var wRead = wrong.ReadShort(d100);
var elapsed = sw2.ElapsedMilliseconds;
Check("读超时返回 null", wRead is null);
Check("LastError 说明通信失败", wrong.LastError is not null && wrong.LastError.Contains("通信失败"), wrong.LastError ?? "");
Check("超时接近设定值（<1500ms）", elapsed < 1500, elapsed + "ms");
Check("失败后 IsConnected = false", !wrong.IsConnected);
wrong.Dispose();

Console.WriteLine("\n12. 重连后恢复");
var again = new ModbusRtuPlc(port, baud, 1, receiveTimeoutMs: 500);
Check("重新 Connect", again.Connect());
Check("恢复后可读 D106（触发计数）", again.ReadShort(ModbusAddressParser.Parse("D106")) is not null);
again.Dispose();

Console.WriteLine("\n=== 结果：通过 " + pass + "，失败 " + fail + " ===");
if (fail > 0)
{
    Console.WriteLine("失败项：");
    foreach (var f in failures) Console.WriteLine("  - " + f);
}
return fail == 0 ? 0 : 1;
