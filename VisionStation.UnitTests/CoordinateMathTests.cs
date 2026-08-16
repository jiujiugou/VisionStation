using VisionStation.Core.Geometry;

namespace VisionStation.UnitTests;

/// <summary>
/// 坐标换算核心算法测试（ADR-003）
/// 锁定 Uniform 布局、Zoom 钳制、双向换算与非法输入保护。
/// 对应 App 层 CoordinateMapper 只是本算法的 WPF 薄封装，本类直接测 Core 算法。
/// </summary>
public class CoordinateMathTests
{
    [Fact]
    public void UpdateLayout_CentersImageUniformly()
    {
        var m = new CoordinateMath();
        // 容器 400x400，图像 200x100 → Uniform 比例 = min(2,4) = 2，垂直居中留 100px 黑边
        m.UpdateLayout(containerW: 400, containerH: 400, pixelW: 200, pixelH: 100);

        Assert.Equal(2.0, m.ScaleFactor);
        Assert.Equal(0.0, m.OffsetX);
        Assert.Equal(100.0, m.OffsetY);
        Assert.Equal(200.0, m.ImagePixelWidth);
        Assert.Equal(100.0, m.ImagePixelHeight);
    }

    [Fact]
    public void ImageToScreen_ScreenToImage_RoundTrip()
    {
        var m = new CoordinateMath();
        m.UpdateLayout(640, 480, 320, 240);

        var (sx, sy) = m.ImageToScreen(100, 50);
        var (ix, iy) = m.ScreenToImage(sx, sy);

        Assert.Equal(100, ix, 3);
        Assert.Equal(50, iy, 3);
    }

    [Fact]
    public void SetZoom_ClampsToRange()
    {
        var m = new CoordinateMath();

        m.SetZoom(100);      // 超上限 → 钳到 20
        Assert.Equal(20.0, m.Zoom);

        m.SetZoom(0.001);    // 超下限 → 钳到 0.05
        Assert.Equal(0.05, m.Zoom);

        m.SetZoom(2.0);      // 正常值原样保留
        Assert.Equal(2.0, m.Zoom);
    }

    [Fact]
    public void Zoom_AffectsScreenToImageMapping()
    {
        var m = new CoordinateMath();
        m.UpdateLayout(200, 200, 100, 100); // ScaleFactor=1, Offset=(50,50)

        // 无缩放：屏幕 (100,100) → 图像 (50,50)
        var (x1, y1) = m.ScreenToImage(100, 100);
        Assert.Equal(50, x1, 3);
        Assert.Equal(50, y1, 3);

        // 2x 缩放：屏幕 (100,100) → 图像 (25,25)
        m.SetZoom(2.0);
        var (x2, y2) = m.ScreenToImage(100, 100);
        Assert.Equal(25, x2, 3);
        Assert.Equal(25, y2, 3);
    }

    [Fact]
    public void InvalidLayout_KeepsPreviousValidScaleFactor()
    {
        var m = new CoordinateMath();
        m.UpdateLayout(400, 400, 200, 200);
        Assert.Equal(2.0, m.ScaleFactor);

        // 非法尺寸（宽或高为 0）→ 不更新比例，保留上一次有效布局，不抛异常
        m.UpdateLayout(400, 400, 0, 0, 0, 0);
        Assert.Equal(2.0, m.ScaleFactor);
        Assert.Equal(400.0, m.ContainerWidth);
    }
}
