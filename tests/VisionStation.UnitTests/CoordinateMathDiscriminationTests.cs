using VisionStation.Core;
using VisionStation.Core.Geometry;
using VisionStation.Core.Models;

namespace VisionStation.UnitTests;

/// <summary>
/// 阶段3 · core-main 切片：变异测试补强（MUTATION_TEST 驱动）。
///
/// 背景：首轮变异测试在 Calibration/CoordinateMath 上存活 17 个变异体，
/// 原因不是断言写错，而是既有用例全用 offset=0、zoom=1、render==container 的退化场景——
/// 加减号互换、乘除互换在这些输入下结果完全相同，测试对它们没有分辨力。
/// 本文件专门用"非零偏移 + 非 1 缩放 + 单一非法参数"来区分这些变异。
/// </summary>
public class CoordinateMathDiscriminationTests
{
    /// <summary>容器 800x600、图像渲染 640x480、像素 640x480 → 比例 1.0，偏移 (80,60)</summary>
    private static CoordinateMath WithOffset()
    {
        var m = new CoordinateMath();
        m.UpdateLayout(800, 600, 640, 480, 640, 480);
        return m;
    }

    [Fact]
    public void NonZeroOffset_IsAppliedWithCorrectSign()
    {
        var m = WithOffset();

        Assert.Equal(1.0, m.ScaleFactor, 6);
        Assert.Equal(80.0, m.OffsetX, 6);
        Assert.Equal(60.0, m.OffsetY, 6);

        var screen = m.ImageToScreen(100, 50);
        Assert.Equal(180.0, screen.X, 6);   // 100*1 + 80
        Assert.Equal(110.0, screen.Y, 6);   // 50*1 + 60

        var image = m.ScreenToImage(180, 110);
        Assert.Equal(100.0, image.X, 6);
        Assert.Equal(50.0, image.Y, 6);
    }

    [Fact]
    public void Zoom_IsMultipliedNotDivided()
    {
        var m = WithOffset();
        m.SetZoom(2.0);

        var screen = m.ImageToScreen(10, 10);
        Assert.Equal(100.0, screen.X, 6);   // 10*1*2 + 80
        Assert.Equal(80.0, screen.Y, 6);    // 10*1*2 + 60

        var image = m.ScreenToImage(100, 80);
        Assert.Equal(10.0, image.X, 6);     // (100-80)/(1*2)
        Assert.Equal(10.0, image.Y, 6);     // (80-60)/(1*2)
    }

    [Fact]
    public void ThreeArgOverload_ComputesUniformScaleFromBothAxes()
    {
        var m = new CoordinateMath();
        m.UpdateLayout(800, 600, 640, 480);   // s = min(1.25, 1.25) = 1.25

        Assert.Equal(1.25, m.ScaleFactor, 6);
        Assert.Equal(0.0, m.OffsetX, 6);
        Assert.Equal(0.0, m.OffsetY, 6);
    }

    [Fact]
    public void ThreeArgOverload_UsesSmallerAxis_WhenAspectRatiosDiffer()
    {
        var m = new CoordinateMath();
        m.UpdateLayout(800, 400, 640, 480);   // min(800/640=1.25, 400/480=0.8333)

        double s = 400.0 / 480.0;
        Assert.Equal(s, m.ScaleFactor, 6);
        // 图像渲染宽 = 640*s = 533.33，容器宽 800 → 左右各留 133.33 黑边
        Assert.Equal((800.0 - 640.0 * s) / 2.0, m.OffsetX, 6);
        Assert.Equal(0.0, m.OffsetY, 6);
    }

    [Theory]
    [InlineData(double.NaN, 600.0, 640.0, 480.0, 640.0, 480.0)]   // 只有容器宽非法
    [InlineData(double.PositiveInfinity, 600.0, 640.0, 480.0, 640.0, 480.0)] // 容器宽为 ∞
    [InlineData(800.0, double.NaN, 640.0, 480.0, 640.0, 480.0)]   // 只有容器高非法
    [InlineData(800.0, 600.0, double.NaN, 480.0, 640.0, 480.0)]   // 只有渲染宽非法
    [InlineData(800.0, 600.0, 640.0, double.NaN, 640.0, 480.0)]   // 只有渲染高非法
    [InlineData(800.0, 600.0, 640.0, 480.0, 0.0, 480.0)]          // 只有像素宽为 0
    [InlineData(800.0, 600.0, 640.0, 480.0, 640.0, 0.0)]          // 只有像素高为 0
    public void AnySingleInvalidArgument_KeepsWholePreviousLayout(
        double cw, double ch, double rw, double rh, double pw, double ph)
    {
        var m = WithOffset();

        m.UpdateLayout(cw, ch, rw, rh, pw, ph);

        Assert.Equal(1.0, m.ScaleFactor, 6);
        Assert.Equal(80.0, m.OffsetX, 6);
        Assert.Equal(60.0, m.OffsetY, 6);
        Assert.Equal(640.0, m.ImagePixelWidth, 6);
        Assert.Equal(480.0, m.ImagePixelHeight, 6);
    }

    [Fact]
    public void Calibration_InvalidScale_MessagePointsAtCalibration()
    {
        var calib = new Calibration();
        calib.SetConfig(new CalibrationConfig { PixelPerMillimeter = 0 });

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => calib.PixelToMm(10));

        Assert.Contains("标定比例", ex.Message);
    }
}
