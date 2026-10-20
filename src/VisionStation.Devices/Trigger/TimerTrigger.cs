namespace VisionStation.Devices;

using System.Timers;

/// <summary>
/// 定时自动触发 — 按配置间隔周期性触发检测
///
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
/// </summary>
public class TimerTrigger : ITriggerSource
{
    private readonly Timer _timer;
    private readonly Action<Exception>? _onError;   // 处理器异常出口（可空：无 DI 场景也能 new）
    private int _running;                          // Timer 是否已 Start（1 = 运行中）
    private int _busy;                             // 0=空闲 1=占用（Interlocked 原子互斥）
    private volatile bool _disposed;

    public event Action? Triggered;

    /// <summary>自动触发是否在运行 —— 包装层（TriggerDevice）据此恢复断开前的状态</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>当前是否有一个触发正在回调里跑（诊断用，反映 CAS 的真实覆盖范围）</summary>
    public bool IsBusy => Volatile.Read(ref _busy) == 1;

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
            try { Triggered?.Invoke(); }
            catch (Exception ex) { _onError?.Invoke(ex); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        };
        _timer.AutoReset = true;
    }

    /// <summary>更新触发间隔（毫秒）—— 0/负数由 System.Timers.Timer 直接拒绝，不静默吞掉</summary>
    public int IntervalMs
    {
        get => (int)_timer.Interval;
        set => _timer.Interval = value;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) == 0) _timer.Start();
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) == 1) _timer.Stop();
    }

    /// <summary>手动触发一次（不依赖定时器，不受 _busy 限制）</summary>
    public void Fire() => Triggered?.Invoke();

    /// <summary>释放底层 Timer 资源</summary>
    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }
}
