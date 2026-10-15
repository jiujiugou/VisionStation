namespace VisionStation.Devices;

using System.Timers;

/// <summary>
/// 定时自动触发 — 按配置间隔周期性触发检测
///
/// 线程安全设计：
///   System.Timers.Timer 的 Elapsed 事件在 ThreadPool 线程上触发。
///   消费者（MainViewModel.ProcessFrame）通过 Dispatcher 切到 UI 线程执行。
///   两个 Tick 可能在不同 ThreadPool 线程上同时到达——
///   用 Interlocked.CompareExchange 保证同一时刻只有一个消费者在跑。
///
///   为什么不放在 UI 线程（DispatcherTimer）？
///     触发源在 Devices 层，不应依赖 WPF Dispatcher。
///     Interlocked 方案与线程模型无关，PlcTrigger 也能复用这个模式。
/// </summary>
public class TimerTrigger : ITriggerSource, IDisposable
{
    private readonly Timer _timer;
    private bool _running;                // Timer 是否已 Start
    private int _busy;                    // 0=空闲 1=占用（Interlocked 原子互斥）

    public event Action? Triggered;

    public TimerTrigger(int intervalMs = 1000)
    {
        _timer = new Timer(intervalMs);
        _timer.Elapsed += (_, _) =>
        {
            // CAS：只有 _busy==0 时才能置为 1。多个线程同时抢，只有一个成功。
            if (Interlocked.CompareExchange(ref _busy, 1, 0) == 1)
                return;

            // try/finally：消费者抛异常也不会死锁，_busy 一定释放。
            try { Triggered?.Invoke(); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        };
        _timer.AutoReset = true;
    }

    /// <summary>更新触发间隔（毫秒）</summary>
    public int IntervalMs
    {
        get => (int)_timer.Interval;
        set => _timer.Interval = value;
    }

    public void Start()
    {
        if (!_running) { _running = true; _timer.Start(); }
    }

    public void Stop()
    {
        if (_running) { _running = false; _timer.Stop(); }
    }

    /// <summary>手动触发一次（不依赖定时器，不受 _busy 限制）</summary>
    public void Fire() => Triggered?.Invoke();

    /// <summary>释放底层 Timer 资源</summary>
    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
