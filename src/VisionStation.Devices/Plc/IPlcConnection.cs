namespace VisionStation.Devices.Plc;

/// <summary>
/// PLC 连接的公共契约 —— TCP 与 RTU 两种传输共用同一套读写语义。
///
/// <para>
/// <b>定位</b>：Devices 层里唯一一处"PLC 长什么样"的定义。上游两个消费者 ——
/// <see cref="PlcTriggerSource"/>（100ms 轮询触发 + 写心跳）与
/// <c>PlcResultWriter</c>（后台线程回写 OK/NG）—— 都只认这个接口，
/// 因此"底下是网线还是串口"这个决定只发生在组合根的 <c>AddDevices</c> 里。
/// </para>
/// </summary>
/// <remarks>
/// 为什么抽这个接口：<see cref="PlcTriggerSource"/>（轮询触发）和
/// PlcResultWriter（回写判定）只关心"读一个寄存器 / 写一个寄存器"，
/// 不关心底下是网线还是串口。抽出来是因为实现数**当前就是 2 个**（Tcp + Rtu），
/// 不是为了"将来可能换别家 PLC" —— 那种理由不成立。
///
/// 线程归属：两种实现都由组合根创建为**单例**，触发源轮询线程（System.Timers 的线程池回调）
/// 与结果回写后台线程共用同一个实例。**并发安全由实现自己负责**，调用方不做额外加锁。
///
/// <para>
/// ⚠ <b>这里曾经写过"Hsl 的底层通信对象内部已串行化"，那句话是错的</b>：
/// 2026-10-25 真串口实测，RTU 对象 30 路并发失败 43/50 笔、<c>IsConnected</c> 被踩成 false，
/// 单线程同量零失败；TCP 对象并发读 16 个寄存器错配 9 个（加锁后为 0）。
/// 结论：**Hsl 不会替你串行化，锁必须由实现自己加**。
/// 两个实现现在都各有一把对象级锁（<c>_sync</c>），一笔完整往返才释放 ——
/// 新增实现时照这个模式做，别看 Hsl 的类型名就假设它是线程安全的。
/// </para>
///
/// 失败约定（与 IDevice.LastError 一致）：Connect/Read/Write 都不抛异常，
/// 失败返回 false / null 并填 <see cref="LastError"/>，让轮询循环按失败继续重试。
///
/// <see cref="IsConnected"/> 的语义是"**最近一笔**通信是否成功"，不是"物理链路是否插着"。
/// 它会随每一笔的结果翻面，上层不要拿它做"该不该发下一笔"的判断依据（那会变成自我实现的预言）。
/// </remarks>
public interface IPlcConnection
{
    /// <summary>最近一笔通信是否成功。断线后为 false，下一笔请求由底层自动重连。</summary>
    bool IsConnected { get; }

    /// <summary>最近一次失败的人话原因；null = 当前没有未恢复的失败。</summary>
    string? LastError { get; }

    /// <summary>建立连接。失败不抛异常，返回 false 并填 LastError。</summary>
    bool Connect();

    void Disconnect();

    /// <summary>读一个 16 位寄存器（保持寄存器 4x / 汇川 D）。失败返回 null。</summary>
    short? ReadShort(PlcAddress address);

    /// <summary>读一个位（线圈 0x / 汇川 M、Y；离散输入 1x / 汇川 X）。失败返回 null。</summary>
    bool? ReadBit(PlcAddress address);

    /// <summary>写一个 16 位寄存器（保持寄存器 4x）。成功返回 true。</summary>
    bool WriteShort(PlcAddress address, short value);

    /// <summary>写一个线圈（0x / 汇川 M、Y）。成功返回 true。</summary>
    bool WriteBit(PlcAddress address, bool value);
}
