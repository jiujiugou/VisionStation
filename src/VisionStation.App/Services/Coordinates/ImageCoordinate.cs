using System.Windows;

namespace VisionStation.App.Services.Coordinates;

/// <summary>
/// 图像坐标系 —— 单位：像素（px）
///
/// 原点：图像左上角 (0, 0)
/// X 轴：向右增长（列号）
/// Y 轴：向下增长（行号）
/// 范围：[0, PixelWidth] × [0, PixelHeight]
///
/// 这是唯一真实坐标——ROI 存储、检测算法、裁切区域全部用这个坐标系。
/// 屏幕坐标和世界坐标都从它换算出来。
/// </summary>
public readonly struct ImageCoordinate
{
    /// <summary>图像像素列号</summary>
    public double X { get; }

    /// <summary>图像像素行号</summary>
    public double Y { get; }

    /// <summary>所属图像的像素宽度（用于范围校验）</summary>
    public double ImageWidth { get; }

    /// <summary>所属图像的像素高度（用于范围校验）</summary>
    public double ImageHeight { get; }

    public ImageCoordinate(double x, double y, double imageWidth, double imageHeight)
    {
        ImageWidth  = imageWidth;
        ImageHeight = imageHeight;
        X = Clamp(x, 0, imageWidth);
        Y = Clamp(y, 0, imageHeight);
    }

    /// <summary>创建时不 Clamp（内部使用，调用方保证合法）</summary>
    internal ImageCoordinate(double x, double y)
    {
        X = x; Y = y;
        ImageWidth = ImageHeight = 0;
    }

    /// <summary>这个坐标是否在图像范围内</summary>
    public bool IsValid => X >= 0 && X <= ImageWidth && Y >= 0 && Y <= ImageHeight;

    public override string ToString() => $"({X:F1}, {Y:F1}) px";

    public Point ToPoint() => new(X, Y);

    public static implicit operator Point(ImageCoordinate c) => new(c.X, c.Y);

    private static double Clamp(double v, double min, double max)
        => Math.Max(min, Math.Min(max, v));
}
