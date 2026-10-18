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

    // ══ 霍夫圆检测参数（HoughModes.Gradient 模式，均可调）══
    /// <summary>累加器分辨率倒数：1=与原图同分辨率，1.2=以 1/1.2 分辨率累加（更快，粗定位够用）</summary>
    public double Dp { get; set; } = 1.2;

    /// <summary>允许检出的两圆圆心最小间距（像素）—— 防止同一个孔被重复检出</summary>
    public double MinDist { get; set; } = 50;

    /// <summary>Canny 高阈值（内部先做边缘检测，低阈值自动取其一半）—— 滤掉弱边缘</summary>
    public double Param1 { get; set; } = 100;

    /// <summary>累加器投票阈值：越小越容易检出圆（含假圆），越大越严格（可能漏检）</summary>
    public double Param2 { get; set; } = 30;

    /// <summary>搜索半径下限（像素）—— 收紧范围可显著提速并排除干扰</summary>
    public int MinRadius { get; set; } = 10;

    /// <summary>搜索半径上限（像素）</summary>
    public int MaxRadius { get; set; } = 200;

    /// <summary>设计直径（毫米）</summary>
    public double DesignedDiameterMm { get; set; } = 10.0;

    /// <summary>允许偏差（毫米）</summary>
    public double ToleranceMm { get; set; } = 0.10;

    /// <summary>工具名，须与配方中 ROI 的 Name 对应（"Dimension"）</summary>
    public string Name => "Dimension";

    /// <summary>
    /// 创建尺寸测量工具
    /// </summary>
    /// <param name="calibration">标定对象（提供 px→mm 换算），依赖注入的核心参数</param>
    /// <param name="designedDiameterMm">设计直径（毫米），默认 10.0</param>
    /// <param name="toleranceMm">公差（毫米），默认 ±0.10</param>
    public DimensionTool(Calibration calibration,
        double designedDiameterMm = 10.0, double toleranceMm = 0.10)
    {
        _calibration = calibration;
        DesignedDiameterMm = designedDiameterMm;
        ToleranceMm = toleranceMm;
    }

    /// <summary>
    /// 执行圆孔直径测量
    ///
    /// 处理流水线（针对 ROI 内的灰度图）：
    ///   1. 裁剪 ROI + 高斯模糊（霍夫圆对边缘噪声敏感，先平滑）
    ///   2. 梯度霍夫圆检测 —— 在 MinRadius~MaxRadius 范围内找圆
    ///   3. 取半径最大的圆 —— 一个 ROI 内通常只有一个待测孔，
    ///      最大圆即目标；小圆多为纹理/杂边缘的误检
    ///   4. 像素直径 → mm（依赖标定的像素当量），与设计值比差值、按公差判 OK/NG
    ///   5. 标注：圆心从 ROI 局部坐标 +roi.X/Y 还原到原图坐标后画圆
    ///
    /// 判定逻辑：|实测 - 设计| ≤ 公差 → OK；否则 NG 超差
    /// </summary>
    /// <param name="grayImage">整幅灰度图（Pipeline 已统一 CvtColor）</param>
    /// <param name="roi">本工具的检测区域（Name 匹配 "Dimension"）</param>
    /// <returns>
    /// OK：直径在公差内；NG：超差 / 未检出圆 / ROI 无效。
    /// Metrics：MeasuredDiameter_mm（实测）、DesignedDiameter_mm（设计）、
    ///          Deviation_mm（偏差，带符号）、PixelDiameter（像素直径，排查标定问题用）
    /// </returns>
    public ToolResult Execute(Mat grayImage, RoiRegion roi)
    {
        // 第1步：裁 ROI；模糊的作用 —— 霍夫圆基于边缘梯度，
        // 金属表面的加工纹理会产生大量碎边缘，不模糊会导致假圆泛滥
        using var roiMat = roi.Crop(grayImage);
        if (roiMat.Empty())
            return ToolResult.Ng("ROI无效");

        using var blurred = new Mat();
        Cv2.GaussianBlur(roiMat, blurred, new Size(5, 5), 0);

        // 第2步：霍夫圆检测（参数含义见属性注释）
        var circles = Cv2.HoughCircles(blurred, HoughModes.Gradient,
            Dp, MinDist, Param1, Param2, MinRadius, MaxRadius);

        // 一个圆都找不到：可能是孔缺失（漏装/堵孔）或参数不合适 —— 两种情况都应人工复核，判 NG
        if (circles.Length == 0)
            return ToolResult.Ng("未检测到圆孔");

        // 第3步：取半径最大的圆 —— 目标孔在 ROI 内直径最大（见方法注释）
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

        // 第4步：px → mm 换算与公差判定
        // PixelToMm 内部用标定得到的像素当量（mm/px）换算 ——
        // 这就是为什么测量结果依赖标定质量：标定不准，此处全线失真
        double pixelDiameter = bestRadius * 2.0;
        double measuredMm = _calibration.PixelToMm(pixelDiameter);
        double deviationMm = measuredMm - DesignedDiameterMm;
        bool isOk = Math.Abs(deviationMm) <= ToleranceMm;

        // 第5步：圆心 ROI 局部坐标 → 原图坐标（标注画在整幅图上）
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
