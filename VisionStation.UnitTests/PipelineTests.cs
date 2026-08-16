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

    // ════════════════════════════════════════════════════════════
    //  ADR-002 端到端回归：合成垫圈图跑完整 Pipeline，
    //  合格件整体必须 OK，缺孔/划痕件整体必须 NG。
    //  此前 DefectTool 把背景+孔误报为缺陷，导致合格件被判 NG。
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Run_FullPipeline_GoodPartOk_BadPartsNg()
    {
        var calibConfig = new CalibrationConfig
        {
            PixelPerMillimeter = 15.0,
            DesignedDiameterMm = 40.0 / 15.0, // 孔40px → 2.67mm
            DimensionToleranceMm = 0.5
        };
        var calibration = new Calibration();
        calibration.SetConfig(calibConfig);

        // 模板从合格件抠孔
        using var good = CreateWasherImage(hasHole: true, holeRadius: 20);
        using var gray = new Mat();
        Cv2.CvtColor(good, gray, ColorConversionCodes.BGR2GRAY);
        using var template = new Mat(gray, new Rect(130, 130, 40, 40));

        var presTool = new PresenceTool(0.70);
        presTool.LoadTemplate(template);
        var dimTool = new DimensionTool(calibration, calibConfig.DesignedDiameterMm, calibConfig.DimensionToleranceMm);
        var defTool = new DefectTool(20, 128)
        {
            ExcludeCenterX = 150,   // 孔排除圆：垫圈中心孔不判缺陷
            ExcludeCenterY = 150,
            ExcludeRadius = 28
        };

        var pipeline = new Pipeline(new List<IVisionTool> { presTool, dimTool, defTool });
        var rois = new List<RoiRegion>
        {
            new() { Name = "Presence",  X = 120, Y = 120, Width = 80,  Height = 80 },
            new() { Name = "Dimension", X = 120, Y = 120, Width = 80,  Height = 80 },
            new() { Name = "Defect",    X = 30,  Y = 30,  Width = 240, Height = 240 }
        };

        // ── 合格件：无孔缺失、无划痕 → 整体 OK ──
        var okReport = pipeline.Run(good, rois, calibConfig);
        Assert.True(okReport.IsOk);
        Assert.True(okReport.DefectResult.IsOk);

        // ── 缺孔件：两条划痕 + 无孔 → 整体 NG ──
        using var missingHole = CreateWasherImage(hasHole: false,
            scratches: new() { (100, 60, 180, 3), (180, 80, 200, 2) });
        var ngReport1 = pipeline.Run(missingHole, rois, calibConfig);
        Assert.False(ngReport1.IsOk);
        Assert.False(ngReport1.DefectResult.IsOk);

        // ── 划痕件：孔偏大(24px) + 一条划痕 → 整体 NG ──
        using var scratched = CreateWasherImage(hasHole: true, holeRadius: 24,
            scratches: new() { (70, 100, 220, 5) });
        var ngReport2 = pipeline.Run(scratched, rois, calibConfig);
        Assert.False(ngReport2.IsOk);
        Assert.False(ngReport2.DefectResult.IsOk);
    }

    /// <summary>构造 300x300 垫圈图：灰底 + 亮盘 +（可选）中心孔 +（可选）划痕</summary>
    private static Mat CreateWasherImage(bool hasHole, int holeRadius = 20,
        List<(int y, int x0, int x1, int amp)>? scratches = null)
    {
        var img = new Mat(300, 300, MatType.CV_8UC3, new Scalar(30, 30, 30));
        Cv2.Circle(img, 150, 150, 120, new Scalar(160, 160, 160), -1);
        if (hasHole)
            Cv2.Circle(img, 150, 150, holeRadius, new Scalar(25, 25, 25), -1);

        if (scratches != null)
        {
            foreach (var (y, x0, x1, amp) in scratches)
                for (int x = x0; x <= x1; x++)
                {
                    int yy = y + (int)(Math.Sin(x * 0.05) * amp);
                    for (int dy = -2; dy <= 2; dy++)
                        img.Set(yy + dy, x, new Vec3b(45, 45, 45));
                }
        }
        return img;
    }

    // 故意抛异常的工具
    private class ThrowingTool : IVisionTool
    {
        public string Name => "Throw";
        public ToolResult Execute(Mat gray, RoiRegion roi)
            => throw new InvalidOperationException("模拟工具异常");
    }
}
