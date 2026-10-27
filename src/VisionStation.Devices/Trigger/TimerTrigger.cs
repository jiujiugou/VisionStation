namespace VisionStation.Devices;

using System.Timers;

/// <summary>
/// 定时自动触发 — 按配置间隔周期性触发检测
///
/// <para>
/// <b>定位</b>：没有 PLC 时的替代触发源（<c>Plc:Enabled=false</c> 时由组合根选中），
/// 也是演示/试跑模式下唯一会自动跑的驱动。它只负责"到点发信号"，
/// 不关心有没有人在听、也不关心下游是否真的检测了 —— 本程序在 VM 模式下
/// 下游本来就是空实现（见 <c>InspectionWorkflow.RunOnceAsync</c> 的说明）。
/// </para>
///
/// <para>
/// <b>归属</b>：组合根 new 出来的 DI 单例，进程级存活；由控制面板"自动模式"开关
/// 调 <see cref="Start"/>/<see cref="Stop"/>，容器释放时 Dispose。
/// 间隔来自 <c>Trigger:TimerIntervalMs</c>（默认 2000ms），启动期已校验必须 &gt; 0。
/// </para>
/// </summary>
/// <remarks>
/// 线程安全设计：
///   System.Timers.Timer 的 Elapsed 事件在 ThreadPool 线程上触发。
///   消费者（InspectionPageViewModel）把后续处理经同步上下文投回 UI 线程，
///   检测本身在 InspectionWorkflow 里走线程池。
///   两个 Tick 可能在不同 ThreadPool 线程上同时到达——
///   用 Interlocked.CompareExchange 保证同一时刻只有一个消费者在跑。
///
///   为什么不放在 UI 线程（DispatcherTimer）？
///     触发源在 Devices 层，不应依赖 WPF Dispatcher。
///     Interlocked 方案与线程模型无关，PlcTrigger 也能复用这个模式。
///
///   注意 CAS 的实际覆盖范围：它只保护"回调进入 → Invoke 返回"这一段。
///   消费者是 async void（本项目就是），它在第一个 await 处就返回了，
///   之后的处理不受本类保护，单消费者要靠消费者侧的 _isProcessing 守卫补足。
///
/// 异常隔离：
///   Elapsed 跑在 ThreadPool 上，未捕获异常 = 进程终止（.NET 默认行为）。
///   处理器抛错必须就地拦下并上报，不能让一个坏帧带走整个检测程序。
/// </remarks>
public class TimerTrigger : ITriggerSource
{
    private readonly Timer _timer;
    private readonly Action<Exception>? _onError;   // 处理器异常出口（可空：无 DI 场景也能 new）
    private int _running;                          // Timer 是否已 Start（1 = 运行中）
    private int _busy;                             // 0=空闲 1=占用（Interlocked 原子互斥）
    private volatile bool _disposed;

    public event Action<TriggerOrigin>? Triggered;

    /// <summary>
    /// 自动触发是否在运行 —— 包装层（TriggerDevice）据此恢复断开前的状态。
    /// <para>
    /// 注意它只反映 <see cref="Start"/>/<see cref="Stop"/> 被调过，**不反映 Dispose**：
    /// Dispose 不会把它清 0，所以释放后再读仍是 true。别用它判断"还能不能用"。
    /// </para>
    /// </summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>当前是否有一个触发正在回调里跑（诊断用，反映 CAS 的真实覆盖范围）</summary>
    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    /// <param name="intervalMs">触发间隔（毫秒）。必须 &gt; 0 —— 构造期就抛，不留一个"永远不响"的定时器。</param>
    /// <param name="onError">消费方异常出口；null = 静默丢弃（只建议测试这么用）</param>
    public TimerTrigger(int intervalMs = 1000, Action<Exception>? onError = null)
    {
        _onError = onError;
        _timer = new Timer(intervalMs);
        _timer.Elapsed += (_, _) =>
        {
            if (_disposed) return;

            // CAS：只有 _busy==0 时才能置为 1。多个线程同时抢，只有一个成功。
            if (Interlocked.CompareExchange(ref _busy, 1, 0) == 1)
                return;

            // try/catch/finally：处理器抛异常既不逃逸到 ThreadPool（那会终止进程），
            // 也不让 _busy 卡住（卡住 = 从此再也不触发）。
            try { Triggered?.Invoke(TriggerOrigin.Timer); }
            catch (Exception ex) { _onError?.Invoke(ex); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        };
        _timer.AutoReset = true;
    }

    /// <summary>
    /// 更新触发间隔（毫秒）—— 0/负数由 System.Timers.Timer 直接拒绝，不静默吞掉。
    /// <para>
    /// 运行中修改立即改的是下一次计时的周期，不会把已经排好的那一次提前；
    /// 想让新间隔立刻生效需要 Stop/Start。
    /// </para>
    /// </summary>
    public int IntervalMs
    {
        get => (int)_timer.Interval;
        set => _timer.Interval = value;
    }

    /// <summary>启动自动触发。幂等：重复调用不会叠加出第二个定时器。</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) == 0) _timer.Start();
    }

    /// <summary>停止自动触发。不等待在途回调 —— 已进入 Elapsed 的那一次会跑完。</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) == 1) _timer.Stop();
    }

    /// <summary>
    /// 手动触发一次（不依赖定时器，不受 _busy 限制）。
    ///
    /// <para>
    /// 因为绕开了 CAS，它**可以和定时触发同时在跑**；也因为没有 try/catch，
    /// 订阅方抛出的异常会直接冒到调用方（UI 线程上就是一次未处理异常）。
    /// 现有唯一调用点是控制面板按钮，订阅方已自行兜住。
    /// </para>
    /// </summary>
    public void Fire() => Triggered?.Invoke(TriggerOrigin.Manual);

    /// <summary>释放底层 Timer 资源。重复调用安全（Timer.Stop/Dispose 本身幂等）。</summary>
    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }
}
