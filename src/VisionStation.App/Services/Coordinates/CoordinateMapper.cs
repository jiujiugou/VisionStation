using System.Windows;
using VisionStation.Core.Geometry;

namespace VisionStation.App.Services.Coordinates;

/// <summary>
/// 坐标双向映射器 —— 屏幕 ↔ 图像（App 层 WPF 薄封装）
///
/// 换算核心算法在 Core 层 <see cref="CoordinateMath"/>（纯数学，可独立单测），
/// 本类只负责两件事：
///   1. 委托：布局更新、缩放、Screen↔Image 双向换算全部转发给 CoordinateMath
///   2. UI 类型适配：提供 Point / Rect / ScreenCoordinate / ImageCoordinate 便捷重载
///
/// 架构约定（ADR-003）：不在这里写任何换算公式，避免 UI 层与算法耦合。
/// 全局单例注入，所有需要坐标换算的地方共用。
/// </summary>
public class CoordinateMapper
{
    // 核心算法（纯数学，可独立单测）
    private readonly CoordinateMath _math = new();

    /// <summary>图像的原始像素宽度</summary>
    public double ImagePixelWidth => _math.ImagePixelWidth;

    /// <summary>图像的原始像素高度</summary>
    public double ImagePixelHeight => _math.ImagePixelHeight;

    /// <summary>外层容器的渲染宽度</summary>
    public double ContainerWidth => _math.ContainerWidth;

    /// <summary>外层容器的高度</summary>
    public double ContainerHeight => _math.ContainerHeight;

    /// <summary>Uniform 拉伸比例 = 图像渲染宽度 ÷ 原始像素宽度</summary>
    public double ScaleFactor => _math.ScaleFactor;

    /// <summary>图像在容器中的水平偏移（Uniform 拉伸后的黑边宽度）</summary>
    public double OffsetX => _math.OffsetX;

    /// <summary>图像在容器中的垂直偏移（Uniform 拉伸后的黑边高度）</summary>
    public double OffsetY => _math.OffsetY;

    /// <summary>用户手动缩放级别（滚轮调），1.0 = 无额外缩放</summary>
    public double Zoom => _math.Zoom;

    /// <summary>
    /// 完整参数更新。
    /// containerW/containerH = Grid 实际宽高
    /// imageRenderW/imageRenderH = Image 控件 Uniform 渲染宽高
    /// pixelW/pixelH = 图像原始像素尺寸
    /// </summary>
    public void UpdateLayout(double containerW, double containerH,
        double imageRenderW, double imageRenderH, double pixelW, double pixelH)
        => _math.UpdateLayout(containerW, containerH, imageRenderW, imageRenderH, pixelW, pixelH);

    /// <summary>仅知像素尺寸时，自动计算 Uniform 参数</summary>
    public void UpdateLayout(double containerW, double containerH, double pixelW, double pixelH)
        => _math.UpdateLayout(containerW, containerH, pixelW, pixelH);

    /// <summary>设置缩放级别（1.0=原始, 最大值20倍）</summary>
    public void SetZoom(double zoom) => _math.SetZoom(zoom);

    /// <summary>屏幕坐标 → 图像坐标（鼠标回调用）</summary>
    public ImageCoordinate ScreenToImage(ScreenCoordinate screen)
    {
        var (x, y) = _math.ScreenToImage(screen.X, screen.Y);
        return new ImageCoordinate(x, y);
    }

    /// <summary>屏幕 Point → 图像坐标（便捷重载）</summary>
    public ImageCoordinate ScreenToImage(Point screenPoint)
        => ScreenToImage(new ScreenCoordinate(screenPoint.X, screenPoint.Y));

    /// <summary>图像坐标 → 屏幕坐标（渲染 ROI 矩形用）</summary>
    public ScreenCoordinate ImageToScreen(ImageCoordinate image)
    {
        var (x, y) = _math.ImageToScreen(image.X, image.Y);
        return new ScreenCoordinate(x, y);
    }

    /// <summary>图像坐标 → 屏幕 Point（便捷重载）</summary>
    public Point ImageToScreenPoint(ImageCoordinate image)
    {
        var (x, y) = _math.ImageToScreen(image.X, image.Y);
        return new Point(x, y);
    }

    /// <summary>图像矩形 → 屏幕矩形（渲染用）</summary>
    public Rect ImageRectToScreen(double imageX, double imageY,
        double imageW, double imageH)
    {
        var (x, y) = _math.ImageToScreen(imageX, imageY);
        return new Rect(x, y, imageW * _math.ScaleFactor * _math.Zoom, imageH * _math.ScaleFactor * _math.Zoom);
    }
}
