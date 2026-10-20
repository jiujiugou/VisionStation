using System.Diagnostics;
using VisionStation.App.Composition.Lifecycle;

namespace VisionStation.App.Tests;

/// <summary>
/// 关闭状态：一个 bool 加一个取消令牌。只测幂等这一条 —— 它是 C6 的一部分，
/// 而 C6 的另一半（连点窗口按钮只收尾一次）在 AppHostTests 里。
/// </summary>
public class ShutdownStateTests
{
    [Fact]
    public void BeginStop_幂等_重复调用不抛且保持已取消()
    {
        var state = new ShutdownState();

        state.BeginStop();
        var exception = Record.Exception(state.BeginStop);

        Assert.Null(exception);
        Assert.True(state.IsStopping);
        Assert.True(state.Token.IsCancellationRequested);
    }
}

/// <summary>
/// 在途工作计数 —— 本片唯一值得单独成类的东西（真实并发语义）。
///
/// 最重要的一条是"不阻塞调用线程"：关窗处理器跑在 UI 线程上，
/// 这里一旦用 Thread.Sleep / Task.Wait，消息泵被占死，在途帧的 continuation
/// 永远排不上，于是必然超时。
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
        // 上界给得宽（1.5s），只证明"确实在等"，而不是立刻返回 false
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
