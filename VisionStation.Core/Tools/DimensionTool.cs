using OpenCvSharp;
using VisionStation.Core.Models;

namespace VisionStation.Core.Tools;

/// <summary>
/// 尺寸测量工具 —— 霍夫圆检测 + px→mm 换算，测量圆孔直径
///
/// 参数（构造注入）：
///   Calibration — 标定对象（px↔mm）
///   DesignedDiameterMm — 设计直径（毫米）
///   ToleranceMm — 允许偏差（毫米）
/// </summary>
public class DimensionTool : IVisionTool
{
    private readonly Calibration _calibration;

    // 霍夫圆检测参数（可调）
    public double Dp { get; set; } = 1.2;
    public double MinDist { get; set; } = 50;
    public double Param1 { get; set; } = 100;
    public double Param2 { get; set; } = 30;
    public int MinRadius { get; set; } = 10;
    public int MaxRadius { get; set; } = 200;

    /// <summary>设计直径（毫米）</summary>
    public double DesignedDiameterMm { get; set; } = 10.0;

    /// <summary>允许偏差（毫米）</summary>
    public double ToleranceMm { get; set; } = 0.10;

    public string Name => "Dimension";

    public DimensionTool(Calibration calibration,
        double designedDiameterMm = 10.0, double toleranceMm = 0.10)
    {
        _calibration = calibration;
        DesignedDiameterMm = designedDiameterMm;
        ToleranceMm = toleranceMm;
    }

    public ToolResult Execute(Mat grayImage, RoiRegion roi)
    {
        using var roiMat = roi.Crop(grayImage);
        if (roiMat.Empty())
            return ToolResult.Ng("ROI无效");

        using var blurred = new Mat();
        Cv2.GaussianBlur(roiMat, blurred, new Size(5, 5), 0);

        var circles = Cv2.HoughCircles(blurred, HoughModes.Gradient,
            Dp, MinDist, Param1, Param2, MinRadius, MaxRadius);

        if (circles.Length == 0)
            return ToolResult.Ng("未检测到圆孔");

        // 取半径最大的圆
        float bestRadius = 0;
        Point2f bestCenter = default;
        foreach (var c in circles)
        {
            if (c.Radius > bestRadius)
            {
                bestRadius = c.Radius;
                bestCenter = c.Center;
            }
        }

        double pixelDiameter = bestRadius * 2.0;
        double measuredMm = _calibration.PixelToMm(pixelDiameter);
        double deviationMm = measuredMm - DesignedDiameterMm;
        bool isOk = Math.Abs(deviationMm) <= ToleranceMm;

        // 圆心转为原图坐标
        var imgCenter = new Point(bestCenter.X + roi.X, bestCenter.Y + roi.Y);

        return new ToolResult
        {
            IsOk = isOk,
            Metrics = new()
            {
                ["MeasuredDiameter_mm"] = Math.Round(measuredMm, 3),
                ["DesignedDiameter_mm"] = DesignedDiameterMm,
                ["Deviation_mm"] = Math.Round(deviationMm, 3),
                ["PixelDiameter"] = Math.Round(pixelDiameter, 2)
            },
            Summary = isOk
                ? $"设计:{DesignedDiameterMm:F2}mm 实测:{measuredMm:F2}mm 偏差:{deviationMm:F3}mm"
                : $"超差! 设计:{DesignedDiameterMm:F2}mm 实测:{measuredMm:F2}mm 偏差:{deviationMm:F3}mm",
            Overlays = new List<DrawCommand>
            {
                new DrawCircle
                {
                    Center = imgCenter,
                    Radius = bestRadius,
                    Color = isOk ? new Scalar(0, 180, 0) : new Scalar(0, 0, 220),
                    Label = isOk ? "OK" : "NG超差",
                    Thickness = 2
                }
            }
        };
    }
}
