namespace VisionStation.Core.Geometry;

/// <summary>
/// 坐标换算核心算法 —— 屏幕 ↔ 图像 双向换算（纯数学，零外部依赖，可独立单测）
///
/// 职责：
///   1. 持有当前布局参数（容器尺寸、图像渲染尺寸、像素尺寸、缩放级别）
///   2. 提供 ScreenToImage / ImageToScreen 双向换算
///   3. 处理 Uniform 拉伸的居中偏移 + Zoom 额外缩放
///
/// 架构约定（ADR-003）：Core 层只放纯算法，App 层 CoordinateMapper 是它的 WPF 薄封装，
/// 只负责 Point/Rect/ScreenCoordinate/ImageCoordinate 等 UI 类型便捷重载，换算逻辑全在本类。
///
/// 换算公式：
///   imageX = (screenX - OffsetX) / (ScaleFactor * Zoom)
///   screenX = imageX * ScaleFactor * Zoom + OffsetX
/// </summary>
public class CoordinateMath
{
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

    /// <summary>
    /// 完整参数更新。
    /// containerW/containerH = Grid 实际宽高
    /// imageRenderW/imageRenderH = Image 控件 Uniform 渲染宽高
    /// pixelW/pixelH = 图像原始像素尺寸
    /// </summary>
    public void UpdateLayout(double containerW, double containerH,
        double imageRenderW, double imageRenderH, double pixelW, double pixelH)
    {
        ContainerWidth = containerW;
        ContainerHeight = containerH;
        ImagePixelWidth = pixelW;
        ImagePixelHeight = pixelH;

        // 非法尺寸保护：不更新换算比例，保留上一次有效布局
        if (pixelW <= 0 || pixelH <= 0 || imageRenderW <= 0 || imageRenderH <= 0)
            return;

        ScaleFactor = imageRenderW / pixelW;
        OffsetX = (containerW - imageRenderW) / 2.0;
        OffsetY = (containerH - imageRenderH) / 2.0;
    }

    /// <summary>仅知像素尺寸时，自动计算 Uniform 参数</summary>
    public void UpdateLayout(double containerW, double containerH, double pixelW, double pixelH)
    {
        double s = Math.Min(containerW / pixelW, containerH / pixelH);
        UpdateLayout(containerW, containerH, pixelW * s, pixelH * s, pixelW, pixelH);
    }

    /// <summary>设置缩放级别（0.05x~20x，超出自动钳制）</summary>
    public void SetZoom(double zoom) => Zoom = Math.Max(0.05, Math.Min(20.0, zoom));

    /// <summary>屏幕坐标 → 图像坐标</summary>
    /// <returns>(imageX, imageY)，比例无效时返回 (0,0)</returns>
    public (double X, double Y) ScreenToImage(double screenX, double screenY)
    {
        double totalScale = ScaleFactor * Zoom;
        if (totalScale <= 0) return (0, 0);
        return (
            (screenX - OffsetX) / totalScale,
            (screenY - OffsetY) / totalScale);
    }

    /// <summary>图像坐标 → 屏幕坐标</summary>
    public (double X, double Y) ImageToScreen(double imageX, double imageY)
    {
        double totalScale = ScaleFactor * Zoom;
        return (
            imageX * totalScale + OffsetX,
            imageY * totalScale + OffsetY);
    }
}
