using OpenCvSharp;
using VisionStation.Core.Models;
using VisionStation.Storage;

namespace VisionStation.UnitTests;

/// <summary>
/// 图片存储服务的失效行为测试。
///
/// 这组测试测的不是"能不能存成功"，而是**存失败时调用方能不能知道**。
/// 原来的实现 Save 返回 string?，"按策略不存"和"存失败了"都返回 null，
/// 调用方无法区分 —— 磁盘满了，产线会以为一切正常，安静地把 NG 图丢光。
/// </summary>
public class ImageStoreTests : IDisposable
{
    private readonly string _root;

    public ImageStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vs-imagestore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响测试结论 */ }
    }

    [Fact]
    public void Save_AllMode_WritesFileAndReturnsPath()
    {
        var svc = new ImageStoreService(_root);
        var report = Report(ok: true);

        var result = svc.Save(Frame(), report);

        Assert.Equal(ImageSaveOutcome.Saved, result.Outcome);
        Assert.True(File.Exists(result.Path));
        Assert.Null(result.Error);
    }

    [Fact]
    public void Save_NGOnlyMode_SkipsOkFrame()
    {
        var svc = new ImageStoreService(_root);
        svc.SetMode(ImageStoreMode.NGOnly);

        var result = svc.Save(Frame(), Report(ok: true));

        // 关键：跳过 ≠ 失败，调用方要能区分
        Assert.Equal(ImageSaveOutcome.Skipped, result.Outcome);
        Assert.False(result.IsFailed);
    }

    [Fact]
    public void Save_NGOnlyMode_SavesNgFrame()
    {
        var svc = new ImageStoreService(_root);
        svc.SetMode(ImageStoreMode.NGOnly);

        var result = svc.Save(Frame(), Report(ok: false));

        Assert.Equal(ImageSaveOutcome.Saved, result.Outcome);
    }

    [Fact]
    public void Save_DateFolderBlockedByFile_ReturnsFailedInsteadOfThrowing()
    {
        // 把当天的日期目录预先占成一个同名文件 —— Directory.CreateDirectory 会抛 IOException。
        // 这类故障（目录被占、路径被改、权限变化）在产线上很常见。
        var svc = new ImageStoreService(_root);
        var dateDir = Path.Combine(_root, DateTime.Now.ToString("yyyy-MM-dd"));
        File.WriteAllText(dateDir, "占位");

        var result = svc.Save(Frame(), Report(ok: true));

        Assert.Equal(ImageSaveOutcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.True(result.IsFailed);
    }

    [Fact]
    public void Save_EmptyImage_ReturnsFailed()
    {
        // 空 Mat 无法编码为 PNG，ImWrite 只会返回 false（不抛异常）。
        // 不看返回值就等于认定"存成功了"。
        var svc = new ImageStoreService(_root);
        var emptyFrame = new CameraFrame { Image = new Mat(), FilePath = "empty.png" };

        var result = svc.Save(emptyFrame, Report(ok: false));

        Assert.Equal(ImageSaveOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void Save_ConsecutiveSuccess_KeepsOutcomeStable()
    {
        var svc = new ImageStoreService(_root);
        for (int i = 0; i < 3; i++)
            Assert.True(svc.Save(Frame(), Report(ok: true)).IsSaved);
    }

    // ── 辅助 ──

    private static CameraFrame Frame()
    {
        // 20x20 灰图：足够小，ImWrite 能正常编码
        var mat = new Mat(20, 20, MatType.CV_8UC1, new Scalar(128));
        return new CameraFrame { Image = mat, FilePath = "test_frame.bmp" };
    }

    private static InspectionReport Report(bool ok)
    {
        var report = new InspectionReport();
        report.SetToolResult("Presence", new Core.Tools.ToolResult { IsOk = ok, Summary = ok ? "OK" : "NG" });
        return report;
    }
}
