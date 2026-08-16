using System.Windows;

namespace VisionStation.App.Services.Coordinates;

/// <summary>
/// 屏幕坐标系 —— 单位：WPF 设备无关像素（dp）
///
/// 原点：Canvas/Grid 渲染区域的左上角 (0, 0)
/// 范围：由容器尺寸决定
///
/// 用途：鼠标事件、UI 布局、ROI 矩形可视化渲染。
/// 屏幕坐标是"临时"的——窗口缩放、平移后立刻失效，不持久化。
/// </summary>
public readonly struct ScreenCoordinate
{
    /// <summary>距离容器左边缘的距离（dp）</summary>
    public double X { get; }

    /// <summary>距离容器上边缘的距离（dp）</summary>
    public double Y { get; }

    /// <summary>容器宽度（用于范围校验）</summary>
    public double ContainerWidth { get; }

    /// <summary>容器高度（用于范围校验）</summary>
    public double ContainerHeight { get; }

    public ScreenCoordinate(double x, double y, double containerWidth, double containerHeight)
    {
        ContainerWidth  = containerWidth;
        ContainerHeight = containerHeight;
        X = Clamp(x, 0, containerWidth);
        Y = Clamp(y, 0, containerHeight);
    }

    /// <summary>创建时不 Clamp</summary>
    internal ScreenCoordinate(double x, double y)
    {
        X = x; Y = y;
        ContainerWidth = ContainerHeight = 0;
    }

    /// <summary>在容器内</summary>
    public bool IsValid => X >= 0 && X <= ContainerWidth && Y >= 0 && Y <= ContainerHeight;

    public override string ToString() => $"({X:F1}, {Y:F1}) dp";

    public Point ToPoint() => new(X, Y);

    public static implicit operator Point(ScreenCoordinate c) => new(c.X, c.Y);

    private static double Clamp(double v, double min, double max)
        => Math.Max(min, Math.Min(max, v));
}
