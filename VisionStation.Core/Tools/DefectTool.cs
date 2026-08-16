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
///   ExcludeCenterX / ExcludeCenterY / ExcludeRadius — 孔排除圆（图像坐标）。
///     零件固有孔（如垫圈中心孔）不是缺陷，落入排除圆内的连通域一律跳过。
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

    /// <summary>孔排除圆圆心 X（图像坐标），默认 -1 = 未启用</summary>
    public double ExcludeCenterX { get; set; } = -1;

    /// <summary>孔排除圆圆心 Y（图像坐标），默认 -1 = 未启用</summary>
    public double ExcludeCenterY { get; set; } = -1;

    /// <summary>孔排除圆半径（像素），≤0 = 未启用</summary>
    public double ExcludeRadius { get; set; } = 0;

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

        // ══ 连通域过滤：只保留"真正的表面缺陷"，剔除背景与固有孔 ══
        // 步骤1：背景必然与 ROI 边界相连 → 剔除贴边组件
        // 步骤2：零件固有孔（如垫圈中心孔）不是缺陷 → 落入孔排除圆的组件剔除
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int compCount = Cv2.ConnectedComponentsWithStats(
            cleaned, labels, stats, centroids, PixelConnectivity.Connectivity8);

        bool exclusionEnabled = ExcludeRadius > 0 && ExcludeCenterX >= 0 && ExcludeCenterY >= 0;

        using var validMask = new Mat(cleaned.Rows, cleaned.Cols, MatType.CV_8UC1, Scalar.All(0));
        using var compMask = new Mat();

        for (int i = 1; i < compCount; i++)
        {
            int left = stats.At<int>(i, (int)ConnectedComponentsTypes.Left);
            int top = stats.At<int>(i, (int)ConnectedComponentsTypes.Top);
            int width = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
            int height = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);

            if (area < AreaMin) continue;

            // 剔除贴 ROI 边界的连通域（背景 / ROI 边缘不完整区域，不判缺陷）
            if (left <= 0 || top <= 0 ||
                left + width >= cleaned.Cols || top + height >= cleaned.Rows)
                continue;

            // 剔除落入孔排除圆的连通域（零件固有孔）
            if (exclusionEnabled)
            {
                double cx = centroids.At<double>(i, 0);
                double cy = centroids.At<double>(i, 1);
                double dist = Math.Sqrt(
                    Math.Pow(cx + roi.X - ExcludeCenterX, 2) +
                    Math.Pow(cy + roi.Y - ExcludeCenterY, 2));
                if (dist <= ExcludeRadius) continue;
            }

            Cv2.InRange(labels, new Scalar(i), new Scalar(i), compMask);
            validMask.SetTo(new Scalar(255), compMask);
        }

        // 在过滤后的掩码上找轮廓 → 判定划痕/缺口
        Cv2.FindContours(validMask, out var contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        int scratchCount = 0, notchCount = 0;
        var overlays = new List<DrawCommand>();

        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);
            double aspectRatio = (double)rect.Width / rect.Height;
            if (aspectRatio < 1.0) aspectRatio = 1.0 / aspectRatio;

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
