namespace VisionStation.Devices;

/// <summary>
/// 触发源接口 —— 决定"什么时候拍一张"
///
/// 三种模式统一在同一个接口里：
///   Fire()  → 手动触发一次（按钮点击）
///   Start() → 启动定时触发（自动模式）
///   Stop()  → 停止定时触发（回到手动）
///   Triggered 事件 → 触发信号出口（InspectionPageViewModel 订阅）
///
/// V1 实现：
///   TimerTrigger：Fire/Start/Stop 全支持
///   将来 PlcTrigger：Start 监听 PLC 寄存器，Fire 软件触发，Stop 断开监听
///
/// 继承 IDisposable 的理由同 ICamera：定时器与轮询线程都持有资源，
/// "可释放"由编译器强制，而不是靠实现类自觉。
/// </summary>
public interface ITriggerSource : IDisposable
{
    /// <summary>
    /// 触发信号。每次触发时调用 Invoke()，并带上**这一次**的来源。
    ///
    /// 为什么带参数而不是让订阅方去问触发源：同一个 TimerTrigger 既能被按钮 Fire（手动），
    /// 也能被定时器打（自动），来源只有"触发那一刻"知道。问触发源拿"当前模式"会在
    /// 边界上答错（刚点完按钮、定时器正好也到点）。
    /// </summary>
    event Action<TriggerOrigin>? Triggered;

    /// <summary>自动触发是否正在运行（包装层据此决定"断开后再连接"要不要恢复）</summary>
    bool IsRunning { get; }

    /// <summary>启动自动触发（如 PLC 开始轮询、定时器开始）</summary>
    void Start();

    /// <summary>停止自动触发</summary>
    void Stop();

    /// <summary>手动触发一次（不依赖自动机制）</summary>
    void Fire();
}
