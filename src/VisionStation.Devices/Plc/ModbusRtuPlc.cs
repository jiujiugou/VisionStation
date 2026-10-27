using System.Globalization;
using System.IO.Ports;
using HslCommunication;
using HslCommunication.ModBus;

namespace VisionStation.Devices.Plc;

/// <summary>
/// 校验方式。只留 Modbus RTU 现场真正会用到的三种，避免把 System.IO.Ports 的
/// 类型漏到上层（App 层不该为了配个串口去引用串口程序集）。
/// </summary>
public enum SerialParity
{
    None = 0,
    Odd = 1,
    Even = 2,
}

/// <summary>停止位。现场只有 1 和 2，OnePointFive 在 Modbus 里基本不出现。</summary>
public enum SerialStopBits
{
    One = 1,
    Two = 2,
}

/// <summary>
/// Modbus RTU（串口）连接的薄封装，与 <see cref="ModbusTcpPlc"/> 一一对应。
///
/// <para>
/// <b>定位</b>：现场主流传输（串口直连 PLC）。上层只有两个消费者 ——
/// <see cref="PlcTriggerSource"/>（100ms 轮询、写心跳）与 <c>PlcResultWriter</c>（后台线程回写 OK/NG），
/// 两者拿到的是**同一个实例**。本类不关心寄存器是干什么用的，只负责"一次完整的读写往返"。
/// </para>
///
/// <para>
/// <b>归属</b>：组合根在 <c>AddDevices</c> 里按 <c>Plc:Transport=Rtu</c> new 出来，DI 单例，活到进程结束；
/// 释放由容器负责（<see cref="Dispose"/> → <see cref="Disconnect"/>）。**不要把它换成 transient** ——
/// 每次解析都开一次串口，第二个实例会直接打不开（串口独占）。
/// </para>
///
/// <para>
/// <b>失败行为</b>：除 <see cref="Connect"/> 内部接住的 <see cref="System.IO.IOException"/> 外，
/// 读写一律**不抛**：失败返回 null/false 并填 <see cref="LastError"/>，
/// 让轮询循环按失败继续重试。抛异常在这里没有意义 —— 调用方除了记日志也做不了别的。
/// </para>
/// </summary>
/// <remarks>
/// 为什么用 Hsl 的 ModbusRtu 而不是自己拼帧：帧头、CRC16、3.5 字符间隔、
/// 异常码这些都是协议细节，自己写一遍只会多一份没人验证过的实现。
/// 这一层只盖两样东西：人话错误（LastError）和区域合法性把关，不往外漏 Hsl 类型。
///
/// 与 TCP 版的差异（都是串口固有，不是偷懒）：
///   1. 串口独占：同一时刻只能有一笔请求在飞。触发源轮询（100ms）和结果回写
///      （后台线程）会撞在一起 —— 必须由**本类自己的对象锁**串行化。
///      （2026-10-25 实测修正：原注释以为 Hsl 底层会串行化，实测 30 路并发失败 43/50 笔，
///        单线程同量零失败 —— 同一串口对象多线程并发调用会互相踩字节流。锁的代价是
///        回写要等当前这笔读完成，最坏 = 一个 ReceiveTimeOut。）
///   2. 没有网络层重连：串口是本地资源，Connect 失败通常是"口被别的程序占了"
///      或"波特率不对"，重试不会自愈。所以失败信息里必须把串口参数原样带出来，
///      现场第一件事就是核对 COMx 和波特率。
///   3. 9600bps 下一笔 8 字节请求 + 7 字节响应约 15ms，100ms 轮询留足余量；
///      但串口卡住时不会像 TCP 那样立刻返回 RST，所以超时必须短（默认 500ms，
///      不是 TCP 那边的 2000ms）—— 否则一次卡顿会让后面几帧判定都排队过期。
///
/// <para>
/// <b>验证记录（可复现，别只信注释）</b>：com0com 虚拟串口对 COM3↔COM4 +
/// <c>scripts/rtu_slave.py</c>（RTU 从站 + 汇川 ST 逻辑模拟）+ <c>tools/RtuProbe</c>，33 项全过；
/// 生产形态 2 路并发 100 笔 335ms 零失败；极端 30 路并发零失败但耗时 21.5s
/// （Windows 时间片下 30 个阻塞线程抢锁的代价，生产只有 2 个线程，不涉及）。
/// 跑法与完整数据见 <c>docs/产线闭环-说明.md</c> §8。
/// </para>
///
/// <para>
/// <b><see cref="_sync"/> 是实例锁而不是静态锁</b>：锁要保护的是"这一个串口对象"的字节流；
/// 用静态锁会把两块互不相干的串口卡绑在一起，一块卡住另一块也停。
/// 代价是同一进程里两个 <see cref="ModbusRtuPlc"/> 指向同一个 COM 口时锁不住彼此 ——
/// 但那种配置本身就打不开第二个口，会先在 <see cref="Connect"/> 失败。
/// </para>
/// </remarks>
public sealed class ModbusRtuPlc : IPlcConnection, IDisposable
{
    private readonly ModbusRtu _client;

    /// <summary>
    /// 串口字节流的互斥锁。**不能省**：PlcTriggerSource 的轮询线程与
    /// PlcResultWriter 的回写线程共用本实例，而 Hsl 的串口对象不是线程安全的。
    /// </summary>
    private readonly object _sync = new();

    /// <summary>连接参数原样保留，错误信息里要用（现场第一件事就是核对串口和波特率）</summary>
    public string PortName { get; }
    public int BaudRate { get; }
    public byte Station { get; }

    /// <summary>人话形式的连接描述，日志/状态栏直接可用，例如 COM3 9600-8-N-1 站号1</summary>
    public string Description { get; }

    public bool IsConnected { get; private set; }

    /// <summary>最近一次失败的人话原因；null = 当前没有未恢复的失败。与 IDevice.LastError 同一约定。</summary>
    public string? LastError { get; private set; }

    /// <param name="portName">串口名，如 COM3</param>
    /// <param name="baudRate">波特率，H5U 出厂默认 9600</param>
    /// <param name="dataBits">数据位，Modbus RTU 固定 8</param>
    /// <param name="parity">校验：None / Odd / Even（H5U 出厂默认 Even，现场以实际配置为准）</param>
    /// <param name="stopBits">停止位：One / Two</param>
    /// <param name="station">从站站号（1~247）</param>
    /// <param name="receiveTimeoutMs">单笔读写超时，串口场景默认 500ms</param>
    public ModbusRtuPlc(string portName, int baudRate, byte station,
        int dataBits = 8, SerialParity parity = SerialParity.None,
        SerialStopBits stopBits = SerialStopBits.One, int receiveTimeoutMs = 500)
    {
        PortName = portName;
        BaudRate = baudRate;
        Station = station;
        Description = $"{portName} {baudRate}-{dataBits}-{ParityChar(parity)}-{StopChar(stopBits)} 站号{station}";

        _client = new ModbusRtu
        {
            Station = station,
            ReceiveTimeOut = receiveTimeoutMs,
        };
        // 串口参数必须在 Connect 之前设好；Hsl 在 Connect 时才真正打开端口
        _client.SerialPortInni(portName, baudRate, dataBits,
            stopBits == SerialStopBits.Two ? StopBits.Two : StopBits.One, ToPortsParity(parity));
    }

    /// <summary>
    /// 打开串口。失败不抛异常，返回 false 并填 LastError——调用方（轮询循环）按失败继续重试。
    ///
    /// <para>
    /// 副作用：成功时才真正打开 COM 口；失败不留半开资源，只留 <see cref="LastError"/>。
    /// 轮询线程每个周期都会在断线时重试它，所以它必须是"失败无害"的。
    /// </para>
    /// </summary>
    public bool Connect()
    {
        // 串口被占用时 Hsl 会抛 IOException（不是返回 OperateResult），这里必须接住：
        // 轮询线程死不得，而且"口被占用"在现场是最常见的一种失败。
        try
        {
            lock (_sync)
            {
                var r = _client.Open();
                if (r.IsSuccess)
                {
                    IsConnected = true;
                    LastError = null;
                    return true;
                }
                IsConnected = false;
                LastError = $"PLC 串口打开失败（{Description}）：{r.Message}";
                return false;
            }
        }
        catch (Exception ex)
        {
            IsConnected = false;
            LastError = $"PLC 串口打开失败（{Description}）：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 关闭串口。幂等；关闭失败也照样把状态置为断开（没有补救动作，如实反映即可）。
    /// 运行期基本不会被调用 —— 串口断了由 <see cref="Connect"/> 在轮询里重开。
    /// </summary>
    public void Disconnect()
    {
        lock (_sync)
        {
            try { _client.Close(); }
            catch (Exception) { /* 关闭失败没有补救动作，如实置状态即可 */ }
            IsConnected = false;
        }
    }

    /// <summary>
    /// 读一个 16 位寄存器（保持寄存器 4x）。失败返回 null 并填 LastError。
    ///
    /// <para>
    /// 区域检查在取锁**之前**做：它不碰 IO，先挡掉非法点位可以少占一次锁窗口。
    /// 代价是这个分支写 <see cref="LastError"/> 时没有锁保护 —— 可能与另一线程的
    /// 写入交错。影响仅限于错误文案可能被后一笔覆盖，不会影响数据正确性。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 读一个位（线圈 0x / 离散输入 1x）。失败返回 null 并填 LastError。
    ///
    /// <para>
    /// 用何种功能码由 <see cref="ModbusArea"/> 决定：0x 走 ReadCoil，1x 走 ReadDiscrete。
    /// 其余区域（保持/输入寄存器）在这里是**明确的错误**而不是"读第 0 位" ——
    /// 按位读寄存器在 Modbus 里要用另一套语义，混用会读到一个看似合理的假值。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 写一个 16 位寄存器（保持寄存器 4x）。成功返回 true。
    ///
    /// <para>
    /// <b>调用方是结果回写线程</b>，最坏要排在当前这笔轮询读之后（一个 ReceiveTimeOut，默认 500ms）。
    /// 这是串口独占的必然代价，不是可以优化的排队 —— 想缩短只能降轮询频率或提高波特率。
    /// 回写方（<c>PlcResultWriter</c>）因此用队列 + 后台线程，不在这条路径上等。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 收敛一笔读写的状态：统一决定 <see cref="IsConnected"/> 与 <see cref="LastError"/>。
    ///
    /// <para>
    /// <b>成功分支必须把 IsConnected 置回 true</b>，这不是可有可无的整洁：
    /// <see cref="IsConnected"/> 的语义是"最近一笔是否成功"，只要有一笔失败就转 false；
    /// 若不在这里恢复，一笔偶发失败之后它会**永久停在 false**，
    /// 状态栏一直显示"PLC 离线"而后面每笔其实都通 —— 状态栏骗人比失败本身更糟。
    /// （2026-10-25 并发实测时就踩过：IsConnected 被并发线程踩成 false 后没拉回来，
    ///   探针里表现为"通信全对、状态全错"。）
    /// </para>
    ///
    /// <para>
    /// <b>调用约束</b>：只在持有 <see cref="_sync"/> 的路径里调用，因此对状态的写入是串行的。
    /// 返回值原样带回 <paramref name="valueOnSuccess"/>，失败时是 null/false ——
    /// 调用方不需要（也不应该）再判一次 <c>IsSuccess</c>。
    /// </para>
    /// </summary>
    private T Settle<T>(OperateResult r, T valueOnSuccess)
    {
        if (r.IsSuccess)
        {
            // 成功要把连接状态置回来：否则一笔偶发失败之后 IsConnected 会永久停在 false，
            // 状态栏一直显示"PLC 离线"，实际后面每笔都通 —— 比失败本身更误导人。
            IsConnected = true;
            LastError = null;
        }
        else
        {
            IsConnected = false;   // 如实反映"这一笔没成"；下一笔由底层自己重开串口
            LastError = $"PLC 通信失败（{Description}）：{r.Message}";
        }
        return valueOnSuccess;
    }

    public void Dispose() => Disconnect();

    /// <summary>寄存器偏移转 Hsl 地址串。不变文化：Modbus 地址没有区域差异可言。</summary>
    private static string Addr(ushort offset) => offset.ToString(CultureInfo.InvariantCulture);

    /// <summary>串口参数里的人话字符：N 无校验 / O 奇 / E 偶。日志和界面都直接显示这个。</summary>
    private static string ParityChar(SerialParity p) => p switch
    {
        SerialParity.Odd => "O",
        SerialParity.Even => "E",
        _ => "N",
    };

    private static string StopChar(SerialStopBits s) => s == SerialStopBits.Two ? "2" : "1";

    private static Parity ToPortsParity(SerialParity p) => p switch
    {
        SerialParity.Odd => Parity.Odd,
        SerialParity.Even => Parity.Even,
        _ => Parity.None,
    };
}
