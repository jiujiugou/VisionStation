using OpenCvSharp;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;

namespace VisionStation.UnitTests;

public class ToolTests
{
    // ── 合成图工具 ──
    private static (Mat image, Mat template) CreateHoleImage()
    {
        var img = new Mat(200, 200, MatType.CV_8UC3, new Scalar(60, 60, 60));
        Cv2.Circle(img, 100, 100, 80, new Scalar(160, 160, 160), -1);
        Cv2.Circle(img, 100, 100, 18, new Scalar(30, 30, 30), -1);

        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
        var template = new Mat(gray, new Rect(82, 82, 36, 36));

        return (img, template);
    }

    // ── PresenceTool ──

    [Fact]
    public void PresenceTool_HoleExists_ReturnsOK()
    {
        var (img, template) = CreateHoleImage();
        var tool = new PresenceTool(0.70);
        tool.LoadTemplate(template);

        var roi = new RoiRegion { Name = "Presence", X = 50, Y = 50, Width = 100, Height = 100 };

        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
        var result = tool.Execute(gray, roi);

        Assert.True(result.IsOk);
        Assert.True(result.Metrics["MatchScore"] > 0.80);
        Assert.Single(result.Overlays);
        Assert.IsType<DrawRect>(result.Overlays[0]);

        img.Dispose(); template.Dispose();
    }

    [Fact]
    public void PresenceTool_NoTemplate_ReturnsNG()
    {
        var tool = new PresenceTool(0.70); // 没加载模板
        var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(128));
        var roi = new RoiRegion { X = 0, Y = 0, Width = 100, Height = 100 };

        var result = tool.Execute(img, roi);

        Assert.False(result.IsOk);
        Assert.Contains("模板未加载", result.Summary);

        img.Dispose();
    }

    // ── DimensionTool ──

    [Fact]
    public void DimensionTool_FindsCircle_ReturnsOK()
    {
        var calib = new Calibration();
        calib.SetConfig(new CalibrationConfig { PixelPerMillimeter = 10.0, DesignedDiameterMm = 3.6, DimensionToleranceMm = 0.5 });
        var tool = new DimensionTool(calib, 3.6, 0.5);

        // 画一个直径20px的圆 → 20/10 = 2.0mm
        var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(30));
        Cv2.Circle(img, 100, 100, 10, new Scalar(200, 200, 200), -1); // 白色圆盘，中心有深色圆孔不太好
        // 实际上应该画深色孔在浅色背景上，霍夫圆找的是边界
        var clean = new Mat(200, 200, MatType.CV_8UC1, Scalar.Black);
        Cv2.Circle(clean, 100, 100, 20, Scalar.White, -1);
        // 不，简单点：直接在图上画一个正圆让霍夫能找到

        var roi = new RoiRegion { X = 0, Y = 0, Width = 200, Height = 200 };
        var result = tool.Execute(clean, roi);

        // 圆的直径约40px → /10 = 4mm，设计3.6±0.5，偏差0.4→OK
        Assert.True(result.IsOk);
        Assert.Single(result.Overlays);
        Assert.IsType<DrawCircle>(result.Overlays[0]);

        img.Dispose(); clean.Dispose();
    }

    // ── DefectTool ──

    [Fact]
    public void DefectTool_CleanSurface_ReturnsOK()
    {
        var tool = new DefectTool(areaMin: 20, thresholdValue: 128);
        var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200)); // 干净纯色
        var roi = new RoiRegion { X = 0, Y = 0, Width = 200, Height = 200 };

        var result = tool.Execute(img, roi);

        Assert.True(result.IsOk);
        Assert.Empty(result.Overlays);

        img.Dispose();
    }

    [Fact]
    public void DefectTool_ScratchedSurface_ReturnsNG()
    {
        var tool = new DefectTool(areaMin: 20, thresholdValue: 128);
        var img = new Mat(200, 200, MatType.CV_8UC1, new Scalar(200)); // 浅色背景

        // 画一条深色划痕
        for (int x = 40; x < 160; x++)
        for (int dy = -2; dy <= 2; dy++)
            img.Set(100 + dy, x, (byte)30);

        var roi = new RoiRegion { X = 0, Y = 0, Width = 200, Height = 200 };
        var result = tool.Execute(img, roi);

        Assert.False(result.IsOk);
        Assert.NotEmpty(result.Overlays);

        img.Dispose();
    }

    // ── DefectTool：垫圈表面（背景 + 固有孔 不应误报）──
    // ADR-002 回归：合格垫圈（亮盘 + 暗孔 + 暗背景）必须判 OK，
    // 背景（贴 ROI 边界）与中心孔（孔排除圆）都不能算缺陷。

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

    [Fact]
    public void DefectTool_WasherSurfaceWithHoleExclusion_ReturnsOK()
    {
        var tool = new DefectTool(areaMin: 20, thresholdValue: 128);
        tool.ExcludeCenterX = 150;
        tool.ExcludeCenterY = 150;
        tool.ExcludeRadius = 28;

        using var img = CreateWasherImage(hasHole: true, holeRadius: 20);
        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);

        // ROI 覆盖整个圆盘（含背景角 + 中心孔），两者都必须被排除
        var roi = new RoiRegion { X = 30, Y = 30, Width = 240, Height = 240 };
        var result = tool.Execute(gray, roi);

        Assert.True(result.IsOk);
        Assert.Empty(result.Overlays);
    }

    [Fact]
    public void DefectTool_WasherSurfaceWithoutHoleExclusion_ReturnsNG()
    {
        // 不加孔排除圆：背景被贴边剔除，但中心孔仍被误报 → NG（证明排除机制的必要性）
        var tool = new DefectTool(areaMin: 20, thresholdValue: 128);

        using var img = CreateWasherImage(hasHole: true, holeRadius: 20);
        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);

        var roi = new RoiRegion { X = 30, Y = 30, Width = 240, Height = 240 };
        var result = tool.Execute(gray, roi);

        Assert.False(result.IsOk);
    }

    [Fact]
    public void DefectTool_WasherScratchOnSurface_ReturnsNG()
    {
        var tool = new DefectTool(areaMin: 20, thresholdValue: 128);
        tool.ExcludeCenterX = 150;
        tool.ExcludeCenterY = 150;
        tool.ExcludeRadius = 28;

        // 合格件 + 一条贯穿表面的划痕
        using var img = CreateWasherImage(hasHole: true, holeRadius: 20,
            scratches: new() { (70, 100, 220, 5) });
        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);

        var roi = new RoiRegion { X = 30, Y = 30, Width = 240, Height = 240 };
        var result = tool.Execute(gray, roi);

        Assert.False(result.IsOk);
        Assert.NotEmpty(result.Overlays);
    }
}
