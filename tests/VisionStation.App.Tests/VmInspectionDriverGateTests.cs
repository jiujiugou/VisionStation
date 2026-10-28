using VisionStation.App.Services.Inspection;
using VisionStation.Core.Models;
using VisionStation.Devices;

namespace VisionStation.App.Tests;

/// <summary>
/// 收帧闸门（SemaphoreSlim Wait(0)）的行为契约 —— 2026-10-28 并发梳理的验收测试。
///
/// <para>
/// 背景：原实现是手写标志位"先 Volatile.Read 判断、再 Interlocked.Exchange 占用"，
/// 两步之间有 TOCTOU 缝隙：两帧同时到可双双过闸并发写库。换成 Wait(0) 原子试占后，
/// 检查与占用是同一个动作。这里钉住三条行为：
/// ① 上一帧未完成时，第二帧被丢弃并计数；
/// ② 处理失败的帧同样计入丢弃（口径：到达但没入账）；
/// ③ 闸门在异常路径也会被释放 —— 否则一帧失败就永久堵死整条链路。
/// </para>
///
/// <para>
/// 测试直接驱动 <c>OnLine</c>（internal + InternalsVisibleTo），不经过真 socket ——
/// 闸门的行为与帧从哪来无关，从测试侧喂帧等价于从网络喂，且不引入端口竞态。
/// </para>
/// </summary>
public class VmInspectionDriverGateTests
{
    private static VmInspectionDriver NewDriver(IInspectionWorkflow workflow)
        => new("127.0.0.1", 0, workflow,
            Array.Empty<VmOutputMapping>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VmInspectionDriver>.Instance,
            TimeSpan.FromSeconds(5));

    [Fact]
    public async Task 上一帧未完成时_第二帧被丢弃并计数()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var driver = NewDriver(new BlockingWorkflow(entered, release));

        // 第一帧：进门后挂住（模拟"正在写库"）
        var first = Task.Run(() => driver.OnLine("OK"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 第二帧：应被闸门挡下，进丢弃计数
        driver.OnLine("OK");
        Assert.Equal(1, driver.DroppedTotal);

        // 放行第一帧，收尾干净
        release.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, driver.ReceivedTotal);
    }

    [Fact]
    public void 处理失败的帧计入丢弃_且闸门被释放_后续帧不受牵连()
    {
        using var driver = NewDriver(new FlakyWorkflow());

        // 第一帧：工作流抛异常 → 计入丢弃
        driver.OnLine("OK");
        Assert.Equal(1, driver.DroppedTotal);

        // 第二帧：闸门必须已被释放（异常路径也要走到 Release），
        // 正常处理 → 丢弃数不涨、到达数照记
        driver.OnLine("OK");
        Assert.Equal(1, driver.DroppedTotal);
        Assert.Equal(2, driver.ReceivedTotal);
    }

    /// <summary>第一帧挂住直到放行；期间 AcceptExternalAsync 未返回。</summary>
    private sealed class BlockingWorkflow(TaskCompletionSource entered, TaskCompletionSource release)
        : IInspectionWorkflow
    {
        public Task<InspectionRunResult?> AcceptExternalAsync(InspectionReport report, CancellationToken ct)
        {
            entered.TrySetResult();
            return release.Task.ContinueWith<InspectionRunResult?>(_ => null, ct,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public Task<InspectionRunResult?> RunOnceAsync(TriggerOrigin origin, CancellationToken ct)
            => Task.FromResult<InspectionRunResult?>(null);
    }

    /// <summary>第一次调用抛异常，之后正常返回 null（无结果可显示）。</summary>
    private sealed class FlakyWorkflow : IInspectionWorkflow
    {
        private int _calls;

        public Task<InspectionRunResult?> AcceptExternalAsync(InspectionReport report, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("模拟写库失败");

            return Task.FromResult<InspectionRunResult?>(null);
        }

        public Task<InspectionRunResult?> RunOnceAsync(TriggerOrigin origin, CancellationToken ct)
            => Task.FromResult<InspectionRunResult?>(null);
    }
}
