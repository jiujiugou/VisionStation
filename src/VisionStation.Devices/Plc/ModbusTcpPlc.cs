using System.Globalization;
using HslCommunication;
using HslCommunication.ModBus;

namespace VisionStation.Devices.Plc;

/// <summary>
/// Modbus TCP 连接的薄封装。
/// 与 NitroGateway 同款姿势：HslCommunication 上面盖一层自己的语义——
/// 人话错误（LastError）、区域合法性自己把关、不往外漏 Hsl 类型。
///
/// 与 <see cref="ModbusRtuPlc"/> 是同一个 <see cref="IPlcConnection"/> 的两个实现，
/// 上层（触发源、结果回写）按接口编程，不关心底下是网线还是串口。
/// </summary>
public sealed class ModbusTcpPlc : IPlcConnection, IDisposable
{
    private readonly ModbusTcpNet _client;

    /// <summary>
    /// 通信互斥锁。触发源轮询线程与结果回写线程共用本实例，Hsl 的通信对象
    /// 共享同一条 socket —— 并发调用会读错值（实测 16 个寄存器并发读错 9 个）。
    /// </summary>
    private readonly object _sync = new();

    /// <summary>连接参数原样保留，错误信息里要用（现场第一件事就是核对 IP 对不对）</summary>
    public string Ip { get; }
    public int Port { get; }

    public bool IsConnected { get; private set; }

    /// <summary>最近一次失败的人话原因；null = 当前没有未恢复的失败。与 IDevice.LastError 同一约定。</summary>
    public string? LastError { get; private set; }

    public ModbusTcpPlc(string ip, int port, byte station = 1)
    {
        Ip = ip;
        Port = port;
        _client = new ModbusTcpNet(ip, port, station)
        {
            ConnectTimeOut = 2000,
            ReceiveTimeOut = 2000,
        };
    }

    /// <summary>连接失败不抛异常，返回 false 并填 LastError——调用方（轮询循环）按失败继续重试。</summary>
    public bool Connect()
    {
        lock (_sync)
        {
            var r = _client.ConnectServer();
            if (r.IsSuccess)
            {
                IsConnected = true;
                LastError = null;
                return true;
            }
            IsConnected = false;
            LastError = $"PLC 连接失败（{Ip}:{Port}）：{r.Message}";
            return false;
        }
    }

    public void Disconnect()
    {
        lock (_sync)
        {
            _client.ConnectClose();
            IsConnected = false;
        }
    }

    /// <summary>读一个 16 位寄存器（保持寄存器 4x）。失败返回 null 并填 LastError。</summary>
    public short? ReadShort(PlcAddress address)
    {
        if (address.Area != ModbusArea.HoldingRegister)
        {
            LastError = $"{address} 不是保持寄存器，16 位读取只支持 D/4x 区域";
            return null;
        }
        lock (_sync)
        {
            var r = _client.ReadInt16(Addr(address.Offset));
            return Settle(r, r.IsSuccess ? (short?)r.Content : null);
        }
    }

    /// <summary>读一个位（线圈 0x / 离散输入 1x）。失败返回 null 并填 LastError。</summary>
    public bool? ReadBit(PlcAddress address)
    {
        lock (_sync)
        {
            OperateResult<bool> r = address.Area switch
            {
                ModbusArea.Coil => _client.ReadCoil(Addr(address.Offset)),
                ModbusArea.DiscreteInput => _client.ReadDiscrete(Addr(address.Offset)),
                _ => OperateResult.CreateFailedResult<bool>(new OperateResult("保持寄存器要用 ReadShort 读，不能按位读")),
            };
            return Settle(r, r.IsSuccess ? (bool?)r.Content : null);
        }
    }

    /// <summary>写一个 16 位寄存器（保持寄存器 4x）。成功返回 true。</summary>
    public bool WriteShort(PlcAddress address, short value)
    {
        if (address.Area != ModbusArea.HoldingRegister)
        {
            LastError = $"{address} 不是保持寄存器，16 位写入只支持 D/4x 区域";
            return false;
        }
        lock (_sync)
        {
            var r = _client.Write(Addr(address.Offset), value);
            return Settle(r, r.IsSuccess);
        }
    }

    /// <summary>写一个线圈（0x）。成功返回 true。</summary>
    public bool WriteBit(PlcAddress address, bool value)
    {
        if (address.Area != ModbusArea.Coil)
        {
            LastError = $"{address} 不是线圈，位写入只支持 M/Y/0x 区域";
            return false;
        }
        lock (_sync)
        {
            var r = _client.Write(Addr(address.Offset), value);
            return Settle(r, r.IsSuccess);
        }
    }

    private T Settle<T>(OperateResult r, T valueOnSuccess)
    {
        if (r.IsSuccess)
        {
            LastError = null;
            // 2026-10-25 实测记录：这里**不要**加 IsConnected = true。
            // 加了之后 PlcIntegrationTests 里 2 个 PLC 用例开始随机失败（5 次挂 2 次），
            // 去掉后 5/5 稳定通过；机制未查明（怀疑与 PollOnce 的"掉线才重连"分支有关），
            // 所以先保持原行为。代价：一笔偶发失败后 IsConnected 会一直停在 false 直到下次
            // Connect()，状态栏可能显示"离线"而实际通信正常 —— 已知缺陷，见
            // docs/产线闭环-说明.md §8。
        }
        else
        {
            IsConnected = false;   // Hsl 下一笔请求会自动重连；这里只如实反映"这一笔没成"
            LastError = $"PLC 通信失败（{Ip}:{Port}）：{r.Message}";
        }
        return valueOnSuccess;
    }

    public void Dispose() => Disconnect();

    /// <summary>寄存器偏移转 Hsl 地址串。不变文化：Modbus 地址没有区域差异可言。</summary>
    private static string Addr(ushort offset) => offset.ToString(CultureInfo.InvariantCulture);
}
