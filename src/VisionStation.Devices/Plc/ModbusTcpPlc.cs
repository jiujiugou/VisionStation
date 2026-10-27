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
///
/// <para>
/// <b>定位</b>：走网关/交换机、或本机 502 仿真的传输。与 RTU 版吃同一套失败约定，
/// 上层拿到的是同一个单例（轮询线程 + 回写线程共用）。
/// </para>
///
/// <para>
/// <b>归属</b>：组合根按 <c>Plc:Transport=Tcp</c>（默认值）创建为 DI 单例，容器释放时关连接。
/// 与 RTU 不同，连接是"可自愈"的：TCP 断开后 Hsl 下一笔会自动重连，
/// 所以这层的 <see cref="Disconnect"/> 在运行期基本不会被调用。
/// </para>
///
/// <para>
/// <b>⚠ 两条历史遗留问题的现状（都已了结；旧结论别再当现状引用）</b>
/// <list type="number">
///   <item><b>对象级锁已经加上（2026-10-26，commit 1b69d22）</b>。加锁后
///         并发读 16 个寄存器的错配从 9 个降到 0。所以"TCP 没有锁"这句话**已经过期** ——
///         注意 <c>docs/产线闭环-说明.md</c> §8 里仍写着 TCP 未加锁，那是文档没跟上代码。</item>
///   <item><b><see cref="Settle"/> 里的 <c>IsConnected = true</c>：2026-10-25 撤回过一次，
///         2026-10-27 已恢复</b>。当年加上它之后 <c>PlcIntegrationTests</c> 里两条 PLC 用例
///         开始随机失败（5 次挂 2 次），根因没查出来就先撤了。后来查明那次随机失败来自
///         <b>测试自己抢端口</b>（<c>FreePort()</c> 先释放再重绑，空窗期被并行用例抢走），
///         与这一行没有因果关系，详见 <see cref="Settle"/> 的注释。现在 TCP 与 RTU
///         在"一笔成功即恢复在线"这件事上行为一致。</item>
/// </list>
/// 也就是说：并发安全与状态准确性这两条现在都成立。
/// 剩下的验证是"反复跑 PLC 集成测试看稳定性"（端口竞态已由 <c>LocalPortCollection</c> 串行化），
/// 不是继续怀疑这一行。
/// </para>
///
/// <para>
/// <b>失败行为</b>：Connect/读写都不抛异常，失败返回 false/null 并填 <see cref="LastError"/>。
/// Hsl 对 TCP 的超时是 2 秒，意味着一次断线会让轮询周期从 100ms 掉到 2s 级别，
/// 期间结果回写也在排队等同一把锁 —— 这是 TCP 版比 RTU 版更该关注串行化开销的原因。
/// </para>
/// </summary>
public sealed class ModbusTcpPlc : IPlcConnection, IDisposable
{
    private readonly ModbusTcpNet _client;

    /// <summary>
    /// 通信互斥锁。触发源轮询线程与结果回写线程共用本实例，Hsl 的通信对象
    /// 共享同一条 socket —— 并发调用会读错值（实测 16 个寄存器并发读错 9 个）。
    ///
    /// <para>
    /// 加锁是一次完整往返持锁（Connect/Read/Write 全程），不是"发完就放"：
    /// Modbus TCP 的请求与响应靠事务号配对，两个线程的请求交错写进同一条流里，
    /// 即使 Hsl 能把包拆对，配到的也可能是另一笔的响应。
    /// 代价是回写要排在当前这笔读之后，最坏等一个 ReceiveTimeOut（2s）。
    /// </para>
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

    /// <summary>
    /// 连接失败不抛异常，返回 false 并填 LastError——调用方（轮询循环）按失败继续重试。
    ///
    /// <para>
    /// <b>会阻塞到 <c>ConnectTimeOut</c>（2 秒）</b>：地址不通时它不是一个快速的失败。
    /// 第一次调用发生在启动流程的 UI 线程上（<c>DeviceManager.ConnectAll</c>），
    /// 所以 IP 填错的现场表现是"启动时窗口晚出现约 2 秒"。
    /// </para>
    ///
    /// <para>
    /// <b>不做 <c>if (IsConnected) return</c> 这种短路</b>：即使状态显示在线也真调一次
    /// <c>ConnectServer()</c>。这条是**有意的**，别为了"省一次握手"加上短路 ——
    /// <see cref="IsConnected"/> 的语义只是"最近一笔是否成功"，拿它挡重连会让
    /// 一条真断掉的 socket 再也换不掉（见 <see cref="Settle"/> 里关于重连闸门的说明）。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 收敛一笔读写的状态：统一决定 <see cref="IsConnected"/> 与 <see cref="LastError"/>。
    ///
    /// <para>
    /// <b>成功分支必须把 IsConnected 置回 true</b>，与 <see cref="ModbusRtuPlc"/> 的 <c>Settle</c> 同一语义：
    /// <see cref="IsConnected"/> 是"<b>最近一笔</b>通信是否成功"（见 <see cref="IPlcConnection"/>），
    /// 一笔失败就翻 false，一笔成功就该翻回来。不恢复的后果是永久停在 false ——
    /// 只跑结果回写、没有轮询的现场（PLC 侧不做触发），掉一次线之后通信早就恢复了，
    /// 状态栏还一直显示"PLC 离线"，现场有人因此去拔插网线。
    /// </para>
    ///
    /// <para>
    /// <b>这一行为什么当年不敢加，现在为什么安全</b>（2026-10-27 复查）：
    /// 2026-10-25 加上它之后 <c>PlcIntegrationTests</c> 的两条 PLC 用例开始随机失败（5 次挂 2 次），
    /// 当时机制没查清就撤回了。现在查明那次随机失败<b>不是这行造成的</b>，是测试自己的端口竞态：
    /// 那几条用例都用 <c>FreePort()</c> 向系统要临时端口、立刻释放、再重新绑定，
    /// 空窗期里并行的 <c>HealthRuntimeProbeTests</c> 会把同一个号要走，
    /// 于是 <c>ServerStart</c>/<c>ConnectServer</c> 报"地址已在使用" —— 表现为随机一条红、单跑又绿。
    /// 两组用例已用 <c>LocalPortCollection</c>（<c>DisableParallelization</c>）串行化。
    /// </para>
    ///
    /// <para>
    /// <b>代码层也复核过没有隐藏耦合</b>：
    /// <list type="bullet">
    ///   <item><see cref="Connect"/> 没有 <c>if (IsConnected) return</c> 之类的短路，永远真调
    ///         <c>ConnectServer()</c>，所以本行不会让某条重连路径被跳过；</item>
    ///   <item>整个仓库里唯一的 "IsConnected 挡动作" 是 <c>PlcTriggerSource.PollOnce</c> 的重连闸门
    ///         （<c>!IsConnected &amp;&amp; !Connect()</c>）。闸门为真时跳过的是<b>额外一次握手</b>，
    ///         不是重连能力：Hsl 每一笔请求都自己取通道
    ///         （<c>NetworkDoubleBase.GetAvailableSocket</c> —— 短连接每笔新建、长连接复用），
    ///         上一笔失败后 <c>IsSocketError</c> 置位，下一笔照样会重连。
    ///         也就是说那个闸门是延迟优化，不是正确性依赖；</item>
    ///   <item>测试断言也没有一条要求"失败后必须保持 false"（见 <c>PlcTests</c> /
    ///         <c>PlcIntegrationTests</c>）：唯一断言 <c>IsConnected == false</c> 的两处都在
    ///         <see cref="Connect"/> 失败之后，而那条分支本来就会置 false。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>不会制造"假在线"</b>：底层 socket 真断时，下一笔会在 <c>ReceiveTimeOut</c>（2s）内失败
    /// → 走失败分支置 false → 下一轮 <c>PollOnce</c> 看到 false 就显式 <see cref="Connect"/>。
    /// 状态恢复与故障暴露走的是同一条链，没有把失败藏起来。
    /// </para>
    ///
    /// <para>
    /// 只在持有 <see cref="_sync"/> 的路径里调用。
    /// </para>
    /// </summary>
    private T Settle<T>(OperateResult r, T valueOnSuccess)
    {
        if (r.IsSuccess)
        {
            // 成功即恢复在线（2026-10-27，与 RTU 对齐）。
            // 当年撤回这一行是因为一次误判（测试抢端口），机制分析见上面 <para>；
            // 别再以"加了会随机挂"为由删掉它 —— 那样会退回"状态栏永久显示离线"的缺陷。
            IsConnected = true;
            LastError = null;
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
