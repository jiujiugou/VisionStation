using VisionStation.App.Composition.Lifecycle;

namespace VisionStation.App.Tests;

/// <summary>
/// L1 证据：关闭顺序、超时边界、异常隔离、幂等（设计文档 §0 的 C5 / C6 / C7）。
///
/// 这些用例只用假对象，不启 WPF、不碰真实容器。它们能证明"代码按约定顺序调用、
/// 单步失败不阻断后续、重复调用不重复执行"，但**不能**证明"进程真的能退出、设备真的释放"
/// —— 后者只能靠 L3 真实进程走查。两层都要，缺一层不算完成。
/// </summary>
public class ShutdownCoordinatorTests
{
    private const string StopTrigger = "stop-trigger";
    private const string CancelLifetime = "cancel-lifetime";
    private const string WaitIdle = "wait-idle";
    private const string Dispose = "dispose";
    private const string Flush = "flush";
    private const string Watchdog = "watchdog";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StopAsync_按固定顺序执行六步()
    {
        var stages = new RecordingStages();

        var result = await new ShutdownCoordinator(stages).StopAsync(Budget);

        // 顺序即 WPF-L1.4 的顺序：先停止摄入，再取消，再等在途，最后才拆资源。
        // 任何两步调换都会在这里红 —— 尤其是"先释放容器再取消"这种会引发
        // 在途帧访问已释放对象的最坏顺序。
        Assert.Equal(
            new[] { StopTrigger, CancelLifetime, WaitIdle, Dispose, Flush, Watchdog },
            stages.Calls);

        Assert.True(result.Completed);
        Assert.False(result.TimedOut);
        Assert.Equal(6, result.Steps.Count);
    }

    [Fact]
    public async Task StopAsync_在途未收尾_仍释放设备并刷日志_但标记未完成()
    {
        // C3 + C7：等不到在途帧可以接受（如实记录），但绝不能因此不释放相机、不刷日志。
        var stages = new RecordingStages { IdleResult = false };

        var result = await new ShutdownCoordinator(stages).StopAsync(Budget);

        Assert.False(result.Completed);
        Assert.Equal(
            new[] { StopTrigger, CancelLifetime, WaitIdle, Dispose, Flush, Watchdog },
            stages.Calls);

        var idle = Assert.Single(result.Steps, s => s.Name == WaitIdle);
        Assert.False(idle.Ok);
    }

    [Fact]
    public async Task StopAsync_某步抛异常_后续步骤照常执行且不向上抛()
    {
        // C7：停触发失败（例如设备驱动抛错）不能把整个关闭流程带走，
        // 否则相机句柄留在那里，下次启动报"设备被占用"。
        var stages = new RecordingStages();
        stages.Throws[StopTrigger] = new InvalidOperationException("设备停不下来");

        var result = await new ShutdownCoordinator(stages).StopAsync(Budget);

        Assert.False(result.Completed);
        Assert.Equal(6, stages.Calls.Count);
        Assert.False(result.Steps[0].Ok);
        Assert.Contains("设备停不下来", result.Steps[0].Error);
    }

    [Fact]
    public async Task StopAsync_重复调用_六步只执行一次()
    {
        // C6：用户连点 X、或系统注销与点 X 同时发生，收尾只能做一遍。
        var stages = new RecordingStages();
        var coordinator = new ShutdownCoordinator(stages);

        var first = await coordinator.StopAsync(Budget);
        var second = await coordinator.StopAsync(Budget);

        Assert.Same(first, second);
        Assert.Equal(6, stages.Calls.Count);
    }

    [Fact]
    public async Task StopAsync_预算为零_不执行任何步骤并标记超时()
    {
        // 预算已经用完时不能"假装还在收尾"：记 TimedOut 并立刻返回，让上层决定是否强退。
        var stages = new RecordingStages();

        var result = await new ShutdownCoordinator(stages).StopAsync(TimeSpan.Zero);

        Assert.True(result.TimedOut);
        Assert.Empty(stages.Calls);
    }

    [Fact]
    public async Task StopAsync_有在途工作_等它结束才继续释放()
    {
        // C3 的正向证据：关闭真的会等，而不是走个过场。
        // 用真实的 InFlightWorkMonitor 而不是假 bool —— 假的只能证明"读到了返回值"，
        // 证明不了"确实在等在途帧"。
        var monitor = new InFlightWorkMonitor();
        var lease = monitor.Begin();
        var stages = new RecordingStages { WaitIdleImpl = timeout => monitor.WhenIdleAsync(timeout) };

        var stopTask = new ShutdownCoordinator(stages).StopAsync(TimeSpan.FromSeconds(5));

        await Task.Delay(100);
        Assert.False(stopTask.IsCompleted);          // 在途没结束，不许放行
        Assert.DoesNotContain(Dispose, stages.Calls); // 更不许提前拆设备

        lease.Dispose();
        var result = await stopTask;

        Assert.True(result.Completed);
        Assert.Contains(Dispose, stages.Calls);
    }

    [Fact]
    public async Task StopAsync_等待在途使用剩余预算()
    {
        // 每一步拿到的必须是"总预算 - 已花时间"，否则单步可以突破总预算之外的时间上界。
        var stages = new RecordingStages();
        var budget = TimeSpan.FromMilliseconds(500);

        await new ShutdownCoordinator(stages).StopAsync(budget);

        Assert.InRange(stages.LastIdleTimeout, TimeSpan.Zero, budget);
    }

    /// <summary>
    /// 六步的假实现：把每次调用写成字符串，方便断言顺序；
    /// 可以按名字注入异常，用来验证"某步失败不影响后续"。
    /// </summary>
    private sealed class RecordingStages : IShutdownStages
    {
        public List<string> Calls { get; } = new();

        public Dictionary<string, Exception> Throws { get; } = new(StringComparer.Ordinal);

        /// <summary>第 3 步的返回值：false 模拟"在途帧等不到"。</summary>
        public bool IdleResult { get; init; } = true;

        /// <summary>换成真实的等待实现（验证"确实在等"而不是读一个假 bool）。</summary>
        public Func<TimeSpan, Task<bool>>? WaitIdleImpl { get; init; }

        public TimeSpan LastIdleTimeout { get; private set; }

        public void StopTrigger() => Record(ShutdownCoordinatorTests.StopTrigger);

        public void CancelAppLifetime() => Record(ShutdownCoordinatorTests.CancelLifetime);

        public Task<bool> WaitForIdleAsync(TimeSpan timeout)
        {
            LastIdleTimeout = timeout;
            Record(ShutdownCoordinatorTests.WaitIdle);
            return WaitIdleImpl?.Invoke(timeout) ?? Task.FromResult(IdleResult);
        }

        public Task DisposeServicesAsync()
        {
            Record(ShutdownCoordinatorTests.Dispose);
            return Task.CompletedTask;
        }

        public void FlushLog() => Record(ShutdownCoordinatorTests.Flush);

        public void NotifyWatchdog() => Record(ShutdownCoordinatorTests.Watchdog);

        private void Record(string name)
        {
            Calls.Add(name);
            if (Throws.TryGetValue(name, out var ex)) throw ex;
        }
    }
}
