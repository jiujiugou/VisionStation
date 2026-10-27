namespace VisionStation.Devices;

/// <summary>
/// 触发源设备 —— 包装 ITriggerSource，实现 IDevice
///
/// <para>
/// <b>定位</b>：适配器。把"触发源"（<see cref="TimerTrigger"/> 或 <see cref="PlcTriggerSource"/>）
/// 塞进 <see cref="IDevice"/> 的形状，让 <see cref="DeviceManager"/> 能像对待一台设备那样
/// 统一连接、统一显示状态。它自己不产生触发，只是转发。
/// </para>
///
/// <para>
/// <b>归属</b>：组合根在 <c>AddDevices</c> 里 new 一次并 <c>DeviceManager.Register</c>，
/// 与 <see cref="ITriggerSource"/> 同为 DI 单例、活到进程结束。
/// 它不被 Dispose —— 真正的资源（Timer、串口）归触发源自己管。
/// 调用都发生在 UI 线程（启动连接、控制面板启停），因此内部没有加锁。
/// </para>
///
/// <para>
/// <b>失败行为</b>：不抛。底层触发源没有返回值可传，失败只体现在
/// <see cref="Status"/> 与 <see cref="LastError"/> 这两行文字上。
/// </para>
///
/// <para>
/// <b>连接状态反映真实 PLC 链路</b>（2026-10-27 修正）。原实现 <c>IsConnected =&gt; true</c> 是硬编码，
/// 导致 <see cref="DeviceManager.AllConnected"/> 与启动自检里"设备连接"那一项
/// <b>永远报 1/1 在线</b> —— PLC 一根线没接也一样。
/// 现在：包装定时器/手工触发源时仍恒 true（它们不是连接型设备，PLC 离线也照常可用）；
/// 包装 PLC 触发源时读底层连接的真实状态。
/// </para>
///
/// <para>
/// <b>为什么 <see cref="Connect"/> 里也要真连一次</b>：只改 <see cref="IsConnected"/> 而不真连，
/// PLC 模式下自检会<b>永久报红</b>（轮询要等操作员点"自动模式"才启动，启动时连接必然没建），
/// 那是把一个假绿灯换成假红灯，操作员很快就学会忽略这一项。
/// 所以启动时按配置真连一次：连上则自检绿、连不上则红，红得有意义。
/// 代价是连不上时启动会多等一个连接超时（TCP 约 2 秒），这条代价
/// <c>DeviceManager.ConnectAll</c> 的注释里已经写明。
/// </para>
///
/// <para>
/// ⚠ <see cref="Type"/> 仍写死 "PLC"，即使实际包装的是 <see cref="TimerTrigger"/>。
/// 现状是"类型栏显示 PLC、状态栏显示自动模式"，两处口径不一致；
/// 因为界面只读 <see cref="Status"/>，所以没造成误判，但新增按 Type 分支的逻辑前要先改这里。
/// </para>
/// </summary>
public class TriggerDevice : IDevice
{
    private readonly ITriggerSource _trigger;

    public string Name => "触发源";
    public string Type => "PLC";

    /// <summary>
    /// 是否可用。PLC 触发源看底层链路；手工与定时触发源恒 true ——
    /// 它们没有"连接"这回事，PLC 断线时定时触发仍然可用。
    /// </summary>
    public bool IsConnected => _trigger is not PlcTriggerSource plc || plc.IsConnected;

    public string Status => _trigger switch
    {
        PlcTriggerSource p => p.IsConnected ? "PLC已连接" : "PLC离线",
        TimerTrigger => "自动模式",
        _ => "手动模式"
    };

    // 手工/定时触发源没有"连接失败"概念；PLC 触发源把底层连接的原因带出来
    public string? LastError => _trigger switch
    {
        PlcTriggerSource p => p.LastError,
        _ => null
    };

    public TriggerDevice(ITriggerSource trigger) => _trigger = trigger;

    // 断开前记住自动模式是否在跑：Connect 是 Disconnect 的逆操作，
    // 旧实现 Connect 为空而 Disconnect 去 Stop，断开再连接后自动触发永久停摆，
    // 界面却仍显示"自动模式"。
    //
    // 现状补充：生产代码里**没有任何一处调用 Disconnect**（DeviceManager.DisconnectAll 没有调用方），
    // 所以"断开 → 恢复"这条路径平时走不到。启动时 ConnectAll 会调一次 Connect，
    // 但那时 _wasRunning 还是 false，等于空操作。
    // 这段逻辑目前只被单测 TriggerDevice_DisconnectThenConnect_RestoresAutoMode 覆盖 ——
    // 留着不是冗余，而是"接口承诺的实现必须自洽"：不能因为暂时没人调就留一个反操作。
    private bool _wasRunning;

    /// <summary>
    /// "连接"触发源 = 恢复断开前的运行状态。
    ///
    /// <para>
    /// <b>PLC 触发源会在这里真连一次链路</b>：轮询要等操作员点"自动模式"才启动，
    /// 启动那一刻连接必然还没建。不在这里连一次，自检的"设备连接"就只能报红
    /// （见类型注释）。连不上不影响后续 —— 轮询启动后会自己重试，
    /// 失败原因由底层连接写进 <see cref="LastError"/>。
    /// </para>
    /// <para>
    /// <b>首次启动时它不会打开自动模式</b>（<c>_wasRunning</c> 初值 false）：
    /// 自动模式必须由操作员在控制面板上主动开启，这是刻意的 ——
    /// 开机就自动跑检测，在没有确认料与光的情况下是事故。
    /// </para>
    /// </summary>
    public void Connect()
    {
        // 真连一次（幂等：已连上时底层直接返回 true，不会重开串口）
        if (_trigger is PlcTriggerSource plc) plc.ConnectedPlc.Connect();

        if (!_wasRunning) return;
        _trigger.Start();
        _wasRunning = false;
    }

    /// <summary>断开 = 停自动模式，并记住"断开前是不是在跑"，供下次 Connect 恢复。</summary>
    public void Disconnect()
    {
        _wasRunning = _trigger.IsRunning;
        _trigger.Stop();
    }
}
