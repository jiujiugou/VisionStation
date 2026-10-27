namespace VisionStation.Devices;

/// <summary>
/// 触发源接口 —— 决定"什么时候拍一张"
///
/// <para>
/// <b>定位</b>：Devices 层的事件源，向上只有一个消费者 ——
/// <c>InspectionPageViewModel</c>（构造时订阅 <see cref="Triggered"/>）。
/// 注意它**不知道检测怎么做**：本层只负责"到点了/PLC 要了/人按了"，然后调事件；
/// 检测链路由订阅方转交给 <c>InspectionWorkflow</c>。
/// </para>
///
/// <para>
/// <b>归属</b>：组合根按配置二选一创建，DI 单例，活到进程结束：
/// <see cref="TimerTrigger"/>（PLC 未启用）或 <see cref="PlcTriggerSource"/>（PLC 启用）。
/// 由 <c>TriggerDevice</c> 包一层交给设备管理器，同时直接注入控制面板用于启停与手动触发。
/// 上层不负责 Dispose —— 容器释放时统一收。
/// </para>
///
/// <para>
/// <b>线程</b>：<see cref="Triggered"/> 的触发方在后台线程（Timer 的线程池回调、PLC 轮询回调），
/// 因此订阅方自己负责切回 UI 线程；本接口不做任何 Dispatcher 相关的事，
/// 否则 Devices 层就会依赖 WPF。
/// </para>
///
/// <para>
/// <b>失败行为</b>：实现内部必须把异常吃掉并通过构造函数注入的 <c>onError</c> 上报 ——
/// 回调跑在线程池上，未捕获异常会直接终止进程（.NET 默认行为）。
/// </para>
/// </summary>
/// <remarks>
/// 继承 IDisposable 的理由：定时器与轮询都持有资源（Timer 句柄、线程池回调、底层连接的使用权），
/// "可释放"由编译器强制，而不是靠实现类自觉。注意两个实现都**不负责关闭 PLC 连接**
/// （连接由组合根持有，结果回写还要用），Dispose 只收自己那份。
/// </remarks>
public interface ITriggerSource : IDisposable
{
    /// <summary>
    /// 触发信号。每次触发时调用 Invoke()，并带上**这一次**的来源。
    ///
    /// 为什么带参数而不是让订阅方去问触发源：同一个 TimerTrigger 既能被按钮 Fire（手动），
    /// 也能被定时器打（自动），来源只有"触发那一刻"知道。问触发源拿"当前模式"会在
    /// 边界上答错（刚点完按钮、定时器正好也到点）。
    ///
    /// <para>
    /// <b>订阅方约束</b>：自动触发路径（Timer/PLC 轮询）在后台线程上、以 <c>Invoke</c> 同步调用；
    /// 手工的 <see cref="Fire"/> 则在调用方线程上同步调用。
    /// 订阅方应当是"发个信号就返回"（现有实现是 async void，在第一个 await 处返回），重活丢给线程池。
    /// 若改成长同步处理，<see cref="PlcTriggerSource"/> 那次 <c>PollOnce</c> 会被堵在 <c>Invoke</c> 里，
    /// 而定时器照常排下一个回调 —— 表现为多个轮询重叠（心跳跳号、沿状态互相覆盖），
    /// 而不是"周期变长"。而且不会报任何错。
    /// </para>
    /// </summary>
    event Action<TriggerOrigin>? Triggered;

    /// <summary>自动触发是否正在运行（包装层据此决定"断开后再连接"要不要恢复）</summary>
    bool IsRunning { get; }

    /// <summary>启动自动触发（如 PLC 开始轮询、定时器开始）。幂等：重复调用不会叠出两个循环。</summary>
    void Start();

    /// <summary>停止自动触发。不等待在途回调结束 —— 已进入回调的那一次会跑完。</summary>
    void Stop();

    /// <summary>手动触发一次（不依赖自动机制）。来源固定为 <see cref="TriggerOrigin.Manual"/>。</summary>
    void Fire();
}
