using System.Windows;

namespace VisionStation.App.Services.Coordinates;

/// <summary>
/// 坐标双向映射器 —— 屏幕 ↔ 图像
///
/// 职责：
///   1. 持有当前布局参数（容器尺寸、图像渲染尺寸、像素尺寸、缩放级别）
///   2. 提供 ScreenToImage / ImageToScreen 双向换算
///   3. 处理 Uniform 拉伸的居中偏移 + Zoom 额外缩放
///
/// 纯数学类，零外部依赖，可独立单元测试。
/// 全局单例注入，所有需要坐标换算的地方共用。
///
/// 换算公式：
///   imageX = (screenX - OffsetX) / (ScaleFactor * Zoom)
///   screenX = imageX * ScaleFactor * Zoom + OffsetX
/// </summary>
public class CoordinateMapper
{
    // ═══════════════════════════════════════════════════
    //  布局参数
    // ═══════════════════════════════════════════════════

    /// <summary>图像的原始像素宽度</summary>
    public double ImagePixelWidth { get; private set; }

    /// <summary>图像的原始像素高度</summary>
    public double ImagePixelHeight { get; private set; }

    /// <summary>外层容器的渲染宽度</summary>
    public double ContainerWidth { get; private set; }

    /// <summary>外层容器的高度</summary>
    public double ContainerHeight { get; private set; }

    /// <summary>Uniform 拉伸比例 = 图像渲染宽度 ÷ 原始像素宽度</summary>
    public double ScaleFactor { get; private set; } = 1.0;

    /// <summary>图像在容器中的水平偏移（Uniform 拉伸后的黑边宽度）</summary>
    public double OffsetX { get; private set; }

    /// <summary>图像在容器中的垂直偏移（Uniform 拉伸后的黑边高度）</summary>
    public double OffsetY { get; private set; }

    /// <summary>用户手动缩放级别（滚轮调），1.0 = 无额外缩放</summary>
    public double Zoom { get; private set; } = 1.0;

    // ═══════════════════════════════════════════════════
    //  布局更新
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// 完整参数更新。
    /// containerW/containerH = Grid 实际宽高
    /// imageRenderW/imageRenderH = Image 控件 Uniform 渲染宽高
    /// pixelW/pixelH = 图像原始像素尺寸
    /// </summary>
    public void UpdateLayout(double containerW, double containerH,
                              double imageRenderW, double imageRenderH,
                              double pixelW, double pixelH)
    {
        ContainerWidth   = containerW;
        ContainerHeight  = containerH;
        ImagePixelWidth  = pixelW;
        ImagePixelHeight = pixelH;

        if (pixelW <= 0 || pixelH <= 0 || imageRenderW <= 0 || imageRenderH <= 0)
            return;

        ScaleFactor = imageRenderW / pixelW;
        OffsetX     = (containerW - imageRenderW) / 2.0;
        OffsetY     = (containerH - imageRenderH) / 2.0;
    }

    /// <summary>
    /// 仅知像素尺寸时，自动计算 Uniform 参数
    /// </summary>
    public void UpdateLayout(double containerW, double containerH, double pixelW, double pixelH)
    {
        double s = Math.Min(containerW / pixelW, containerH / pixelH);
        UpdateLayout(containerW, containerH, pixelW * s, pixelH * s, pixelW, pixelH);
    }

    // ═══════════════════════════════════════════════════
    //  Zoom（滚轮缩放）
    // ═══════════════════════════════════════════════════

    /// <summary>设置缩放级别（1.0=原始, 最大值20倍）</summary>
    public void SetZoom(double zoom) => Zoom = Math.Max(0.05, Math.Min(20.0, zoom));

    // ═══════════════════════════════════════════════════
    //  坐标换算
    // ═══════════════════════════════════════════════════

    /// <summary>屏幕坐标 → 图像坐标（鼠标回调用）</summary>
    public ImageCoordinate ScreenToImage(ScreenCoordinate screen)
    {
        double totalScale = ScaleFactor * Zoom;
        if (totalScale <= 0) return new ImageCoordinate(0, 0);
        return new ImageCoordinate(
            (screen.X - OffsetX) / totalScale,
            (screen.Y - OffsetY) / totalScale);
    }

    /// <summary>屏幕 Point → 图像坐标（便捷重载）</summary>
    public ImageCoordinate ScreenToImage(Point screenPoint)
        => ScreenToImage(new ScreenCoordinate(screenPoint.X, screenPoint.Y));

    /// <summary>图像坐标 → 屏幕坐标（渲染 ROI 矩形用）</summary>
    public ScreenCoordinate ImageToScreen(ImageCoordinate image)
    {
        double totalScale = ScaleFactor * Zoom;
        return new ScreenCoordinate(
            image.X * totalScale + OffsetX,
            image.Y * totalScale + OffsetY);
    }

    /// <summary>图像坐标 → 屏幕 Point（便捷重载）</summary>
    public Point ImageToScreenPoint(ImageCoordinate image)
    {
        double totalScale = ScaleFactor * Zoom;
        return new Point(
            image.X * totalScale + OffsetX,
            image.Y * totalScale + OffsetY);
    }

    /// <summary>图像矩形 → 屏幕矩形（渲染用）</summary>
    public Rect ImageRectToScreen(double imageX, double imageY,
                                   double imageW, double imageH)
    {
        double totalScale = ScaleFactor * Zoom;
        return new Rect(
            imageX * totalScale + OffsetX,
            imageY * totalScale + OffsetY,
            imageW * totalScale,
            imageH * totalScale);
    }
}
