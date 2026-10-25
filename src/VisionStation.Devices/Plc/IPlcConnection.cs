namespace VisionStation.Devices.Plc;

/// <summary>
/// PLC 连接的公共契约 —— TCP 与 RTU 两种传输共用同一套读写语义。
/// </summary>
/// <remarks>
/// 为什么抽这个接口：<see cref="PlcTriggerSource"/>（轮询触发）和
/// PlcResultWriter（回写判定）只关心"读一个寄存器 / 写一个寄存器"，
/// 不关心底下是网线还是串口。抽出来是因为实现数**当前就是 2 个**（Tcp + Rtu），
/// 不是为了"将来可能换别家 PLC" —— 那种理由不成立。
///
/// 线程归属：两种实现都由组合根创建为单例，触发源轮询线程与结果回写后台线程
/// 共用同一个实例。实现自己负责并发安全（Hsl 的底层通信对象内部已串行化），
/// 调用方不做额外加锁。
///
/// 失败约定（与 IDevice.LastError 一致）：Connect/Read/Write 都不抛异常，
/// 失败返回 false / null 并填 <see cref="LastError"/>，让轮询循环按失败继续重试。
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
