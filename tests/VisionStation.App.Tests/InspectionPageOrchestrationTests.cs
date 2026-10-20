using VisionStation.App.Composition.Lifecycle;
using VisionStation.App.Services.Inspection;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Tests;

/// <summary>
/// 检测页的编排契约：触发怎么进、并发怎么去重、关闭怎么收尾。
///
/// 这些是 S4 从 <c>MainViewModel</c> 搬到页面 VM 的行为，全部是"并发/生命周期"问题，
/// 只能在这一层测：工作流本身没有线程逻辑（它只负责在后台跑完一帧）。
///
/// 测试里同步上下文传 null，触发就地执行 —— 所以可以在 Fire() 之后直接断言，
/// 不需要 Application / Dispatcher。
/// </summary>
public class InspectionPageOrchestrationTests
{
    [Fact]
    public void 关闭开始后_触发不再进入工作流()
    {
        var trigger = new FakeTriggerSource();
        var camera = new FakeCamera();
        var shutdown = new ShutdownState();
        var monitor = new InFlightWorkMonitor();
        var workflow = new FakeWorkflow { Result = TestResults.Frame() };

        var page = TestPages.Inspection(workflow, trigger, camera, shutdown, monitor);

        // 先关，再触发：这一帧根本不该开始
        shutdown.BeginStop();
        trigger.Fire();

        Assert.Equal(0, workflow.RunCount);
        Assert.Equal(0, camera.CaptureCount);
        Assert.Equal(0, monitor.Count);

        page.Dispose();
    }

    [Fact]
    public void 正常触发_跑一帧_结果广播给面板_租约归还()
    {
        var trigger = new FakeTriggerSource();
        var monitor = new InFlightWorkMonitor();
        var workflow = new FakeWorkflow { Result = TestResults.Frame(ok: true, "Presence", "Dimension") };

        var page = TestPages.Inspection(workflow, trigger, camera: null, shutdown: null, inFlight: monitor);

        trigger.Fire();

        Assert.Equal(1, workflow.RunCount);

        // 面板由消息驱动：图、结果行、状态栏都是订阅者自己更新的
        Assert.NotNull(page.ImagePanel.OriginalImage);
        Assert.NotNull(page.ImagePanel.AnnotatedImage);
        Assert.Equal(2, page.ResultPanel.Rows.Count);
        Assert.Equal("OK", page.ResultPanel.VerdictText);

        // 在途租约必须归还，否则关闭流程会一直等到超时
        Assert.Equal(0, monitor.Count);

        page.Dispose();
    }

    [Fact]
    public async Task 两个触发并发_第二帧被丢弃而不是排队()
    {
        var gate = new TaskCompletionSource<InspectionRunResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trigger = new FakeTriggerSource();
        var monitor = new InFlightWorkMonitor();
        var workflow = new FakeWorkflow { Gate = gate };

        var page = TestPages.Inspection(workflow, trigger, camera: null, shutdown: null, inFlight: monitor);

        trigger.Fire();   // 第一帧挂在 gate 上
        trigger.Fire();   // 第二帧应被丢弃

        Assert.Equal(1, workflow.RunCount);

        // 在途计数为 1：关闭流程看到的就是这个数字，它证明"确实在等"
        Assert.Equal(1, monitor.Count);

        gate.SetResult(TestResults.Frame());
        await WaitUntilAsync(() => monitor.Count == 0);

        // 丢弃而不是排队：跑完仍然只有一帧
        Assert.Equal(1, workflow.RunCount);
        Assert.Equal(2, trigger.FireCount);   // 两次触发都进来了，只是第二次没落到工作流上

        page.Dispose();
    }

    [Fact]
    public async Task 关闭时_在途帧拿到的令牌被取消_且异常不外泄()
    {
        var gate = new TaskCompletionSource<InspectionRunResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trigger = new FakeTriggerSource();
        var shutdown = new ShutdownState();
        var monitor = new InFlightWorkMonitor();
        var workflow = new FakeWorkflow { Gate = gate };

        var page = TestPages.Inspection(workflow, trigger, camera: null, shutdown: shutdown, inFlight: monitor);

        trigger.Fire();
        Assert.Equal(1, workflow.RunCount);

        // 关闭：令牌取消 → 工作流里的等待抛 OperationCanceledException →
        // 页面 VM 必须吞掉并归还租约（不吞就是 Crash，不归还就是关窗卡满 8 秒）
        shutdown.BeginStop();

        await WaitUntilAsync(() => monitor.Count == 0);

        Assert.Equal(1, workflow.RunCount);
        Assert.True(workflow.LastToken.IsCancellationRequested);

        page.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
            await Task.Delay(10);

        Assert.True(condition(), "等待条件超时");
    }
}
