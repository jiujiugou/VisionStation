using OpenCvSharp;
using VisionStation.Core.Models;

namespace VisionStation.Core.Tools;

/// <summary>
/// 表面缺陷检测工具 —— 灰度+滤波+阈值+Blob分析，检出划痕和缺口
///
/// 参数（构造注入）：
///   AreaMin — 最小缺陷面积（像素），小于此值忽略
///   ThresholdValue — 二值化阈值
///   ScratchAspectRatioMin — 划痕长宽比判定阈值
///   NotchCircularityMax — 缺口圆形度判定阈值
/// </summary>
public class DefectTool : IVisionTool
{
    /// <summary>最小缺陷面积（像素）</summary>
    public double AreaMin { get; set; } = 50;

    /// <summary>二值化阈值</summary>
    public double ThresholdValue { get; set; } = 128;

    /// <summary>划痕判定：长宽比 > 此值</summary>
    public double ScratchAspectRatioMin { get; set; } = 3.0;

    /// <summary>缺口判定：圆形度 < 此值</summary>
    public double NotchCircularityMax { get; set; } = 0.6;

    public string Name => "Defect";

    public DefectTool(double areaMin = 50, double thresholdValue = 128)
    {
        AreaMin = areaMin;
        ThresholdValue = thresholdValue;
    }

    public ToolResult Execute(Mat grayImage, RoiRegion roi)
    {
        using var roiMat = roi.Crop(grayImage);
        if (roiMat.Empty())
            return ToolResult.Ng("ROI无效");

        // 高斯去噪 + 二值化
        using var blurred = new Mat();
        Cv2.GaussianBlur(roiMat, blurred, new Size(5, 5), 0);

        using var binary = new Mat();
        Cv2.Threshold(blurred, binary, ThresholdValue, 255, ThresholdTypes.BinaryInv);

        // 形态学去噪
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using var cleaned = new Mat();
        Cv2.MorphologyEx(binary, cleaned, MorphTypes.Open, kernel);

        // 找轮廓 → 判定划痕/缺口
        Cv2.FindContours(cleaned, out var contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        int scratchCount = 0, notchCount = 0;
        var overlays = new List<DrawCommand>();

        foreach (var contour in contours)
        {
            double area = Cv2.ContourArea(contour);
            if (area < AreaMin) continue;

            var rect = Cv2.BoundingRect(contour);
            double aspectRatio = (double)rect.Width / rect.Height;
            if (aspectRatio < 1.0) aspectRatio = 1.0 / aspectRatio;

            double perimeter = Cv2.ArcLength(contour, true);
            double circularity = perimeter > 0
                ? (4 * Math.PI * area) / (perimeter * perimeter) : 0;

            // 轮廓点 → 原图坐标
            var points = contour.Select(p => new Point(p.X + roi.X, p.Y + roi.Y)).ToArray();

            bool isScratch = aspectRatio >= ScratchAspectRatioMin;
            if (isScratch) scratchCount++; else notchCount++;

            overlays.Add(new DrawContour
            {
                Points = points,
                Color = isScratch
                    ? new Scalar(0, 0, 255)    // 红色=划痕
                    : new Scalar(0, 165, 255),  // 橙色=缺口
                Thickness = 2
            });
        }

        int total = scratchCount + notchCount;
        return new ToolResult
        {
            IsOk = total == 0,
            Metrics = new()
            {
                ["ScratchCount"] = scratchCount,
                ["NotchCount"] = notchCount,
                ["TotalContours"] = contours.Length
            },
            Summary = total == 0
                ? "表面无缺陷"
                : $"划痕:{scratchCount} 缺口:{notchCount} 总数:{total}",
            Overlays = overlays
        };
    }
}
