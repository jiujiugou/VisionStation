using Microsoft.Extensions.Logging;
using VisionStation.Devices;

namespace VisionStation.App.Composition.Lifecycle;

/// <summary>
/// <see cref="IShutdownStages"/> 的真实实现 —— 把六步映射到真实依赖。
///
/// 本类故意写得很薄：没有判断、没有重试、没有顺序决策（那些都在 Coordinator 里）。
/// 薄的好处是它可以被结构性测试盯住：例如"第 4 步必须走后台卸荷钩子"。
/// </summary>
public sealed class AppShutdownStages : IShutdownStages
{
    private readonly IServiceProvider _services;
    private readonly ITriggerSource _trigger;
    private readonly IAppLifetime _lifetime;
    private readonly IInFlightWorkMonitor _inFlight;
    private readonly ILogShutdown _log;
    private readonly IWatchdogSignal _watchdog;
    private readonly Func<Action, Task> _offloadToBackground;
    private readonly ILogger? _logger;

    /// <param name="offloadToBackground">
    /// 第 4 步的线程卸荷钩子，默认 <see cref="Task.Run(Action)"/>。
    /// 做成注入点是为了让"必须切后台线程"这条约束可被测试钉住 —— 直接在当前线程 Dispose，
    /// HousekeepingService 内部那 5 秒同步等待会冻死 UI（设计文档 §5.2）。
    /// </param>
    public AppShutdownStages(
        IServiceProvider services,
        ITriggerSource trigger,
        IAppLifetime lifetime,
        IInFlightWorkMonitor inFlight,
        ILogShutdown log,
        IWatchdogSignal watchdog,
        Func<Action, Task>? offloadToBackground = null,
        ILogger? logger = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _inFlight = inFlight ?? throw new ArgumentNullException(nameof(inFlight));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _watchdog = watchdog ?? throw new ArgumentNullException(nameof(watchdog));
        _offloadToBackground = offloadToBackground ?? (action => Task.Run(action, CancellationToken.None));
        _logger = logger;
    }

    /// <summary>1. 停止接收新的定时触发。已在执行的回调由第 2 步的令牌拦住。</summary>
    public void StopTrigger()
    {
        _trigger.Stop();
        _logger?.LogInformation("[关闭] 触发源已停止");
    }

    /// <summary>2. 取消应用级令牌：已排队但尚未开始的触发会被 <c>MainViewModel</c> 拒绝。</summary>
    public void CancelAppLifetime()
    {
        _lifetime.BeginStop();
        _logger?.LogInformation("[关闭] 应用级取消已发出");
    }

    /// <summary>
    /// 3. 等待在途帧。这里必须真异步（monitor 内部用 TCS，不阻塞线程）。
    /// 刻意不传 <see cref="IAppLifetime.Stopping"/>：那个令牌在第 2 步已经取消，
    /// 传进去会让等待立刻抛/立即返回，等于没等。
    /// </summary>
    public Task<bool> WaitForIdleAsync(TimeSpan timeout) =>
        _inFlight.WhenIdleAsync(timeout, CancellationToken.None);

    /// <summary>
    /// 4. 释放设备与容器。必须切后台线程：容器里 HousekeepingService.Dispose 会同步等最多 5 秒，
    /// 在 UI 线程上执行等于关窗时把界面冻住。
    /// </summary>
    public Task DisposeServicesAsync() =>
        _offloadToBackground(() =>
        {
            if (_services is IDisposable disposable) disposable.Dispose();
            _logger?.LogInformation("[关闭] 设备与容器已释放");
        });

    public void FlushLog() => _log.CloseAndFlush();

    public void NotifyWatchdog() => _watchdog.NotifyCleanExit();
}
