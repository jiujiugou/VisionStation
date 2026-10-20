using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using VisionStation.App.Composition.Lifecycle;

namespace VisionStation.App.Tests;

/// <summary>
/// L1 证据：应用级取消令牌（C2 的基础）。
/// 关窗时这张令牌是"停止接收新工作"的唯一事实源，它必须幂等、不可回滚。
/// </summary>
public class AppLifetimeTests
{
    [Fact]
    public void 未调用BeginStop_不认为正在关闭()
    {
        var lifetime = new AppLifetime();

        Assert.False(lifetime.IsStopping);
        Assert.False(lifetime.Stopping.IsCancellationRequested);
    }

    [Fact]
    public void BeginStop_幂等_重复调用不抛且保持已取消()
    {
        var lifetime = new AppLifetime();

        lifetime.BeginStop();
        var exception = Record.Exception(() => lifetime.BeginStop());

        Assert.Null(exception);
        Assert.True(lifetime.IsStopping);
        Assert.True(lifetime.Stopping.IsCancellationRequested);
    }
}

/// <summary>
/// L1 证据：在途工作计数（C3 的观测手段）。
///
/// 最重要的一条是"不阻塞调用线程"：关窗处理器跑在 UI 线程上，
/// 如果这里用 Thread.Sleep / Task.Wait，UI 消息泵被占死，在途帧的 continuation
/// 永远排不上，于是必然超时（设计文档 §5.1）。
/// </summary>
public class InFlightWorkMonitorTests
{
    [Fact]
    public async Task 没有租约时_立即完成()
    {
        var monitor = new InFlightWorkMonitor();

        var idle = await monitor.WhenIdleAsync(TimeSpan.FromMilliseconds(50));

        Assert.True(idle);
    }

    [Fact]
    public async Task 最后一租约释放后才完成()
    {
        var monitor = new InFlightWorkMonitor();
        var first = monitor.Begin();
        var second = monitor.Begin();
        Assert.Equal(2, monitor.Count);

        var wait = monitor.WhenIdleAsync(TimeSpan.FromSeconds(2));

        first.Dispose();
        Assert.False(wait.IsCompleted);   // 还有一个在跑，不能提前放行

        second.Dispose();
        Assert.True(await wait);          // 归零必须唤醒等待者，而不是靠轮询
        Assert.Equal(0, monitor.Count);
    }

    [Fact]
    public async Task 租约不释放_超时返回false且不占死调用线程()
    {
        var monitor = new InFlightWorkMonitor();
        using var lease = monitor.Begin();
        var sw = Stopwatch.StartNew();

        var idle = await monitor.WhenIdleAsync(TimeSpan.FromMilliseconds(80));

        Assert.False(idle);
        // 上界给得很宽（1.5s），只证明"确实是在等，而不是立刻返回 false"
        Assert.InRange(sw.ElapsedMilliseconds, 50, 1500);
    }

    [Fact]
    public void Count_跟随租约增减()
    {
        var monitor = new InFlightWorkMonitor();
        using (monitor.Begin())
        {
            using (monitor.Begin())
                Assert.Equal(2, monitor.Count);
            Assert.Equal(1, monitor.Count);
        }

        Assert.Equal(0, monitor.Count);
    }
}

/// <summary>
/// L1 证据：六步到真实依赖的映射（C5 / C10）。
/// 这一层不测顺序（顺序在 Coordinator 里），只测"每一步真的调到了对应对象"。
/// </summary>
public class AppShutdownStagesTests
{
    [Fact]
    public async Task 六步都映射到真实依赖()
    {
        var trigger = new FakeTriggerSource();
        var lifetime = new AppLifetime();
        var monitor = new InFlightWorkMonitor();
        var log = new FakeLogShutdown();
        var watchdog = new FakeWatchdogSignal();
        await using var provider = new ServiceCollection().BuildServiceProvider();

        var stages = new AppShutdownStages(provider, trigger, lifetime, monitor, log, watchdog);

        stages.StopTrigger();
        stages.CancelAppLifetime();
        var idle = await stages.WaitForIdleAsync(TimeSpan.FromMilliseconds(50));
        await stages.DisposeServicesAsync();
        stages.FlushLog();
        stages.NotifyWatchdog();

        Assert.Equal(1, trigger.StopCount);
        Assert.True(lifetime.IsStopping);
        Assert.True(idle);
        Assert.Equal(1, log.Count);
        Assert.Equal(1, watchdog.Count);
    }

    [Fact]
    public async Task 释放容器经后台卸荷钩子执行()
    {
        // C10：HousekeepingService.Dispose 内部会同步等最多 5 秒。
        // 第 4 步必须走"后台卸荷"钩子；直接在当前线程 Dispose 就会冻 UI。
        // 断言的是"钩子被调用"，而不是"线程 ID 不同" —— 后者受线程池复用影响，会假红。
        var offloadCount = 0;
        var disposed = false;

        var provider = new ServiceCollection()
            .AddSingleton<IDisposable>(_ => new FakeDisposable(() => disposed = true))
            .BuildServiceProvider();

        // 必须解析一次：容器只释放"它创建/解析过"的单例。
        // 这也说明生产环境的一个前提 —— 要保证相机、Housekeeping 这类资源在启动期真被解析过，
        // 否则关闭时容器根本不知道它们存在（S2.4 调整 ConnectAll 时要注意别把解析推迟到关闭之后）。
        _ = provider.GetRequiredService<IDisposable>();

        var stages = new AppShutdownStages(
            provider, new FakeTriggerSource(), new AppLifetime(), new InFlightWorkMonitor(),
            new FakeLogShutdown(), new FakeWatchdogSignal(),
            offloadToBackground: action =>
            {
                offloadCount++;
                action();
                return Task.CompletedTask;
            });

        await stages.DisposeServicesAsync();

        Assert.Equal(1, offloadCount);
        Assert.True(disposed);
    }
}
