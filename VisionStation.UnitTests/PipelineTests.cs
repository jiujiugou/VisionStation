using OpenCvSharp;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;

namespace VisionStation.UnitTests;

public class PipelineTests
{
    // 生成一张 300x300 灰底 + 白色圆盘的测试图
    private static Mat CreateTestImage()
    {
        var img = new Mat(300, 300, MatType.CV_8UC3, new Scalar(60, 60, 60));
        Cv2.Circle(img, 150, 150, 120, new Scalar(180, 180, 180), -1);
        Cv2.Circle(img, 150, 150, 20, new Scalar(30, 30, 30), -1); // 孔
        return img;
    }

    // 扣出孔模板
    private static Mat CreateHoleTemplate()
    {
        using var full = CreateTestImage();
        using var gray = new Mat();
        Cv2.CvtColor(full, gray, ColorConversionCodes.BGR2GRAY);
        return new Mat(gray, new Rect(130, 130, 40, 40));
    }

    [Fact]
    public void Run_WithPresenceTool_OK()
    {
        var img = CreateTestImage();
        var template = CreateHoleTemplate();

        var tool = new PresenceTool(0.70);
        tool.LoadTemplate(template);

        var pipeline = new Pipeline(new List<IVisionTool> { tool });
        var rois = new List<RoiRegion> { new() { Name = "Presence", X = 120, Y = 120, Width = 80, Height = 80 } };
        var calib = new CalibrationConfig();

        var report = pipeline.Run(img, rois, calib);

        Assert.True(report.PresenceResult.IsOk);
        Assert.Contains("匹配度", report.PresenceResult.Summary);
        Assert.Single(report.PresenceResult.Overlays);
        Assert.IsType<DrawRect>(report.PresenceResult.Overlays[0]);

        img.Dispose(); template.Dispose();
    }

    [Fact]
    public void Run_ToolThrows_ContinuesExecution()
    {
        // 工具执行时抛异常 → Pipeline 不崩，返回 NG 结果
        var tool = new ThrowingTool();
        var pipeline = new Pipeline(new List<IVisionTool> { tool });
        var img = CreateTestImage();
        var rois = new List<RoiRegion> { new() { Name = "Throw", X = 0, Y = 0, Width = 100, Height = 100 } };

        var report = pipeline.Run(img, rois, new CalibrationConfig());

        Assert.False(report.IsOk);
        Assert.Contains("异常", report.GetToolResult("Throw").Summary);

        img.Dispose();
    }

    [Fact]
    public void Run_ROIMatchedByOrder_WhenNameDiffers()
    {
        // ROI 名字对不上时，按顺序匹配（第0个Tool → 第0个ROI）
        var tool = new PresenceTool(0.70);
        tool.LoadTemplate(CreateHoleTemplate());
        var pipeline = new Pipeline(new List<IVisionTool> { tool });
        var img = CreateTestImage();

        // ROI 名不是 "Presence"
        var rois = new List<RoiRegion> { new() { Name = "SomeOtherName", X = 120, Y = 120, Width = 80, Height = 80 } };

        var report = pipeline.Run(img, rois, new CalibrationConfig());

        // 按顺序匹配 → 工具仍然执行
        Assert.True(report.PresenceResult.IsOk);

        img.Dispose();
    }

    // 故意抛异常的工具
    private class ThrowingTool : IVisionTool
    {
        public string Name => "Throw";
        public ToolResult Execute(Mat gray, RoiRegion roi)
            => throw new InvalidOperationException("模拟工具异常");
    }
}
