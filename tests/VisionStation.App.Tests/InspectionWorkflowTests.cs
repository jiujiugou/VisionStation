using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using VisionStation.App.Services.Inspection;
using VisionStation.Devices;
using VisionStation.Core.Engines;
using VisionStation.Core.Models;
using VisionStation.Storage;

namespace VisionStation.App.Tests;

/// <summary>
/// 检测工作流（S4a 从 MainViewModel 搬出来的那段）的契约。
///
/// 这一段没有线程逻辑（线程在 RunOnceAsync 的 Task.Run 上，页面 VM 负责编排），
/// 所以能纯本地测：采图失败、正常一帧、存图失败、取消。
///
/// 有一条特别值钱：**帧的 Mat 由工作流释放**。改造前没人释放，
/// 全靠 Mat 终结器兜底，12MP 彩色帧每分钟数百 MB 的非托管分配，GC 追不上。
/// </summary>
public class InspectionWorkflowTests
{
    [Fact]
    public async Task 采不到图_返回null_不抛异常()
    {
        using var env = new TempEnv(withFrame: false);
        var workflow = env.CreateWorkflow();

        var result = await workflow.RunOnceAsync(TriggerOrigin.Manual, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, env.Camera.CaptureCount);
    }

    [Fact]
    public async Task 正常一帧_报告与位图齐全_帧被释放_报告入库()
    {
        using var env = new TempEnv();
        var workflow = env.CreateWorkflow();

        var result = await workflow.RunOnceAsync(TriggerOrigin.Manual, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result!.Report);
        Assert.NotNull(result.OriginalImage);
        Assert.NotNull(result.AnnotatedImage);

        // 位图必须已冻结：它要跨线程交给 UI
        Assert.True(result.OriginalImage!.IsFrozen);
        Assert.True(result.AnnotatedImage!.IsFrozen);

        Assert.True(result.ImageSaved);
        Assert.Null(result.ImageError);
        Assert.Equal(1, env.Repository.SaveCount);

        // 帧的 Mat 归工作流所有：返回时已经释放，否则每帧漏一份非托管内存
        Assert.NotNull(env.LastFrame);
        Assert.True(env.LastFrame!.Image.IsDisposed, "工作流必须释放采集帧的 Mat");
    }

    [Fact]
    public async Task 存图连续失败_计数累加且给出原因_检测结果仍返回()
    {
        using var env = new TempEnv();
        var workflow = env.CreateWorkflow(blockImageSaving: true);

        var first = await workflow.RunOnceAsync(TriggerOrigin.Manual, CancellationToken.None);
        var second = await workflow.RunOnceAsync(TriggerOrigin.Manual, CancellationToken.None);

        // 存图失败不该让整帧结果作废：报告仍然有价值，但失败必须被看见
        Assert.NotNull(first);
        Assert.False(first!.ImageSaved);
        Assert.NotNull(first.ImageError);
        Assert.Equal(1, first.ConsecutiveImageFailures);
        Assert.Equal(2, second!.ConsecutiveImageFailures);
    }

    [Fact]
    public async Task 已取消的令牌_直接抛取消_不采图()
    {
        using var env = new TempEnv();
        var workflow = env.CreateWorkflow();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => workflow.RunOnceAsync(TriggerOrigin.Manual, cts.Token));

        Assert.Equal(0, env.Camera.CaptureCount);
    }

    /// <summary>一次用的临时环境：图像目录、假仓储、可配相机。</summary>
    private sealed class TempEnv : IDisposable
    {
        private readonly string _root;

        public TempEnv(bool withFrame = true)
        {
            _root = Path.Combine(Path.GetTempPath(), "VisionStation.WorkflowTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            // 每次采图新建一帧：工作流会把上一帧释放掉，复用同一个 Mat 会二次释放。
            // 同时记下最后一帧，测试才能断言"它被释放了"。
            if (withFrame) Camera.FrameFactory = NextFrame;
        }

        public FakeCamera Camera { get; } = new();

        public FakeInspectionRepository Repository { get; } = new();

        /// <summary>
        /// 假引擎：返回一份"一项 OK"的报告。
        /// 用 FakeEngine 而不是真 ONNX 引擎，是因为这里测的是工作流的职责
        /// （尺寸校验、存图计数、帧释放、入库），不是推理质量。
        /// </summary>
        public FakeEngine Engine { get; } = new()
        {
            Report = BuildReport()
        };

        private static InspectionReport BuildReport()
        {
            var report = new InspectionReport { Engine = "FakeEngine" };
            report.SetToolResult("缺陷", ToolResult.Ok("未检出缺陷"));
            return report;
        }

        public CameraFrame? LastFrame { get; private set; }

        private CameraFrame? NextFrame()
        {
            LastFrame = new CameraFrame
            {
                Image = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(32)),
                FilePath = "frame.png"
            };
            return LastFrame;
        }

        public InspectionWorkflow CreateWorkflow(bool blockImageSaving = false)
        {
            string imageFolder = Path.Combine(_root, "images");
            var store = new ImageStoreService(imageFolder);

            if (blockImageSaving)
            {
                // 构造成功之后再把目录换成同名文件：Save 里的 CreateDirectory 必然失败。
                // 对应真实产线上的"目录被删/被占/权限变化"，而且不用改产品代码就能注入。
                Directory.Delete(imageFolder, true);
                File.WriteAllText(imageFolder, "not a directory");
            }

            return new InspectionWorkflow(
                Camera,
                Engine,
                Repository,
                store,
                new RecipeConfig(),
                NullLogger<InspectionWorkflow>.Instance);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { /* 清理失败不影响断言 */ }
        }
    }
}
