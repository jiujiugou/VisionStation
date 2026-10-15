using VisionStation.Core;
using VisionStation.Core.Models;

namespace VisionStation.UnitTests;

/// <summary>
/// 标定换算测试（ADR-003）—— 锁定 px<->mm 双向换算与公差判定，防止标定逻辑被改坏。
/// </summary>
public class CalibrationTests
{
    private static Calibration CreateCalib(double pxPerMm, double designedMm, double toleranceMm)
    {
        var calib = new Calibration();
        calib.SetConfig(new CalibrationConfig
        {
            PixelPerMillimeter = pxPerMm,
            DesignedDiameterMm = designedMm,
            DimensionToleranceMm = toleranceMm
        });
        return calib;
    }

    [Fact]
    public void PixelToMm_ConvertsCorrectly()
    {
        var calib = CreateCalib(15.0, 2.67, 0.5);
        Assert.Equal(4.0, calib.PixelToMm(60), 6);
    }

    [Fact]
    public void MmToPixel_ConvertsCorrectly()
    {
        var calib = CreateCalib(15.0, 2.67, 0.5);
        Assert.Equal(60.0, calib.MmToPixel(4), 6);
    }

    [Fact]
    public void PixelToMm_MmToPixel_RoundTrip()
    {
        var calib = CreateCalib(15.82, 10.0, 0.10);
        Assert.Equal(100.0, calib.MmToPixel(calib.PixelToMm(100)), 6);
    }

    [Fact]
    public void IsWithinTolerance_InsideRange_True()
    {
        var calib = CreateCalib(15.0, 2.67, 0.5); // 范围 [2.17, 3.17]
        Assert.True(calib.IsWithinTolerance(2.60));
        Assert.True(calib.IsWithinTolerance(2.17)); // 下边界
        Assert.True(calib.IsWithinTolerance(3.17)); // 上边界
    }

    [Fact]
    public void IsWithinTolerance_OutsideRange_False()
    {
        var calib = CreateCalib(15.0, 2.67, 0.5);
        Assert.False(calib.IsWithinTolerance(2.00));
        Assert.False(calib.IsWithinTolerance(3.50));
    }
}
