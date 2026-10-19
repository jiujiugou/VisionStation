using OpenCvSharp;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;

namespace VisionStation.UnitTests;

/// <summary>
/// 阶段3 · core-main 切片：R-206 贴边抑制必须留痕。
/// 判定策略不变（贴边仍不计入缺陷），但被丢掉的信号必须能从 Metrics 看出来。
/// </summary>
public class DefectEdgeTests
{
    [Fact]
    public void DefectTool_EdgeTouchingBlob_ReportsSuppressedCount()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        // 一块贴左边界的暗块：面积达标（40x40），但连通域 left=0
        Cv2.Rectangle(img, new Rect(0, 80, 40, 40), new Scalar(20), -1);

        var tool = new DefectTool(areaMin: 50, thresholdValue: 128);
        var roi = new RoiRegion { Name = "Defect", X = 0, Y = 0, Width = 200, Height = 200 };

        var result = tool.Execute(img, roi);

        Assert.True(result.Metrics.ContainsKey("EdgeSuppressed"),
            $"贴边抑制没有留痕，Metrics=[{string.Join(",", result.Metrics.Keys)}]");
        Assert.True(result.Metrics["EdgeSuppressed"] >= 1);
        // 策略不变：贴边仍不算缺陷，判定仍是 OK
        Assert.True(result.IsOk);
    }

    [Fact]
    public void DefectTool_CenterBlob_NoEdgeSuppression()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200));
        Cv2.Rectangle(img, new Rect(80, 80, 40, 40), new Scalar(20), -1);

        var tool = new DefectTool(areaMin: 50, thresholdValue: 128);
        var roi = new RoiRegion { Name = "Defect", X = 0, Y = 0, Width = 200, Height = 200 };

        var result = tool.Execute(img, roi);

        Assert.Equal(0, result.Metrics.GetValueOrDefault("EdgeSuppressed"));
        Assert.False(result.IsOk);   // 中心缺陷照常检出
    }
}
