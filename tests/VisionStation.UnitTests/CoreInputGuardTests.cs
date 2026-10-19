using OpenCvSharp;
using VisionStation.Core;
using VisionStation.Core.Geometry;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;

namespace VisionStation.UnitTests;

/// <summary>
/// 阶段3 · core-main 切片：参数与几何守卫。
/// R-204 标定比例；R-205 布局参数；R-207 尺寸参数；R-208 模板可解码；R-209 YOLO 参数。
/// </summary>
public class CoreInputGuardTests
{
    // ── R-204：标定比例非法必须显式失败，不得返回 ∞/NaN ──

    [Theory]
    [InlineData(0.0)]
    [InlineData(-15.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Calibration_InvalidPixelPerMm_Throws(double ppmm)
    {
        var calib = new Calibration();
        calib.SetConfig(new CalibrationConfig { PixelPerMillimeter = ppmm });

        Assert.Throws<ArgumentOutOfRangeException>(() => calib.PixelToMm(10));
        Assert.Throws<ArgumentOutOfRangeException>(() => calib.MmToPixel(1));
    }

    // ── R-205：非法布局必须整体拒绝，不允许 NaN 穿过守卫 ──

    [Fact]
    public void CoordinateMath_ZeroPixelWidth_KeepsPreviousFiniteLayout()
    {
        var m = new CoordinateMath();
        m.UpdateLayout(640, 480, 640, 480);
        var scale = m.ScaleFactor;
        var ox = m.OffsetX;
        var oy = m.OffsetY;

        m.UpdateLayout(640, 480, 0, 480);   // 0 * ∞ = NaN 的入口

        Assert.Equal(scale, m.ScaleFactor);
        Assert.Equal(ox, m.OffsetX);
        Assert.Equal(oy, m.OffsetY);
        var (x, y) = m.ScreenToImage(100, 100);
        Assert.False(double.IsNaN(x));
        Assert.False(double.IsNaN(y));
    }

    [Fact]
    public void CoordinateMath_NaNInput_KeepsPreviousFiniteLayout()
    {
        var m = new CoordinateMath();
        m.UpdateLayout(640, 480, 640, 480);
        var scale = m.ScaleFactor;

        m.UpdateLayout(double.NaN, double.NaN, 320, 240, 320, 240);

        Assert.Equal(scale, m.ScaleFactor);
        Assert.False(double.IsNaN(m.OffsetX));
        Assert.False(double.IsNaN(m.OffsetY));
    }

    [Fact]
    public void CoordinateMath_ThreeArgOverload_ZeroPixelSize_DoesNotProduceNaN()
    {
        var m = new CoordinateMath();
        m.UpdateLayout(640, 480, 640, 480);

        m.UpdateLayout(640, 480, 0, 0);      // 三参重载的内部 0/0

        Assert.False(double.IsNaN(m.ScaleFactor));
        var (x, y) = m.ScreenToImage(10, 10);
        Assert.False(double.IsNaN(x));
        Assert.False(double.IsNaN(y));
    }

    // ── R-207：尺寸工具参数构造即校验 ──

    [Fact]
    public void DimensionTool_InvalidParameters_Throw()
    {
        var calib = new Calibration();
        calib.SetConfig(new CalibrationConfig());

        Assert.Throws<ArgumentOutOfRangeException>(() => new DimensionTool(calib, 10.0, 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DimensionTool(calib, 10.0, -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DimensionTool(calib, 0.0, 0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DimensionTool(calib, double.NaN, 0.1));
    }

    // ── R-208：模板不可解码必须归类为"模板无效"，不能每帧抛异常 ──

    [Fact]
    public void PresenceTool_CorruptTemplateFile_ReportsInvalidWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vs-bad-{Guid.NewGuid():N}.bmp");
        File.WriteAllText(path, "this is not a bitmap at all");
        try
        {
            var tool = new PresenceTool(0.70);
            tool.LoadTemplate(path);

            using var img = new Mat(100, 100, MatType.CV_8UC1, new Scalar(128));
            var roi = new RoiRegion { Name = "Presence", X = 0, Y = 0, Width = 50, Height = 50 };

            var result = tool.Execute(img, roi);   // 关键：不得抛

            Assert.False(result.IsOk);
            Assert.Contains("模板无效", result.Summary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── R-209：YOLO 参数越界必须立刻失败，不得静默"未检出" ──

    [Fact]
    public void YoloTool_InvalidThresholds_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnnxYoloTool("model.onnx", 1.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnnxYoloTool("model.onnx", 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnnxYoloTool("model.onnx", 0.25f, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnnxYoloTool("model.onnx", 0.25f, 1.5f));
    }

    [Fact]
    public void YoloTool_InvalidLimits_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnnxYoloTool("model.onnx") { MaxDetections = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnnxYoloTool("model.onnx") { ClassNames = Array.Empty<string>() });
    }
}
