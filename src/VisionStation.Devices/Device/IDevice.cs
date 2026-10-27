namespace VisionStation.Devices;

/// <summary>
/// 设备统一接口 —— 产线上每个物理设备的抽象
///
/// <para>
/// <b>定位</b>：只被 <see cref="DeviceManager"/> 的注册表与启动自检消费。
/// 它不承载任何业务语义（不知道触发、不知道判定），只回答四件事：
/// 叫什么、什么类型、在不在、最近为什么不在。
/// </para>
///
/// <para>
/// <b>⚠ 现在只剩 1 个实现：<see cref="TriggerDevice"/></b>（相机层 2026-10-26 已整体删除，
/// 图归视觉平台，程序内没有相机设备可管）。按"抽象数 = 1 时就该砍"的原则，
/// 这个接口本来是可砍的，它现在站得住的理由只有一条：
/// <b>设备统一生命周期管理</b> —— 启动时统一 <c>Connect</c> 并统一收集失败原因（<see cref="Name"/> +
/// <see cref="LastError"/>）、自检统一算"在线/总数"（<see cref="IsConnected"/>）。
/// 这两处确实需要按同一形状遍历一个集合，否则 <see cref="DeviceManager"/> 就得写死"我管的是触发源"。
/// </para>
///
/// <para>
/// 但也要知道这层抽象现在很薄：顶栏那颗 PLC 灯**不经过这里**
/// （<c>TopBarViewModel</c> 直接读触发源的 <c>IsConnected</c>），
/// <see cref="Status"/> 目前只有单测在断言。所以别把它介绍成"设备框架" ——
/// 它是"让启动与自检不必认识具体设备"的一层适配。
/// 真要到第二台设备（比如光源）落地，它才会从"可疑"变成"划算"；
/// 在那之前，注意别再往里加用不上的成员。
/// </para>
/// </summary>
public interface IDevice
{
    /// <summary>设备名称（显示在界面和日志里）。必须唯一且稳定 —— 它是失败信息的定位键。</summary>
    string Name { get; }

    /// <summary>设备类型：当前只会出现 "PLC"（TriggerDevice 写死）；若将来新增设备再扩充取值。</summary>
    string Type { get; }

    /// <summary>
    /// 是否在线。**注意这是快照，不是事件** —— 调用方读到的永远是"上一笔操作之后的结论"，
    /// 不做任何探测。所以它适合做展示与自检，不适合做"能不能发下一笔"的判断。
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 状态摘要（一行文字，如 "PLC已连接" / "PLC离线" / "自动模式"）。
    /// 设计意图是直接上状态栏；**当前生产代码没有消费点**（顶栏那盏灯走的是另一条路），只有单测在断言它。
    /// </summary>
    string Status { get; }

    /// <summary>
    /// 最近一次失败的原因（人话，可直接展示）；null = 当前没有未恢复的失败。
    /// 自检/DeviceManager 用它替代"已调用 Connect 但仍未连接"/"有设备离线"这类没有信息量的句子。
    /// </summary>
    string? LastError { get; }

    /// <summary>连接设备。**失败不抛**（由实现自己 catch 并填 LastError），否则一次设备缺席会带走整个启动。</summary>
    void Connect();

    /// <summary>断开设备。允许在未连接时调用（实现自行忽略）。</summary>
    void Disconnect();
}
