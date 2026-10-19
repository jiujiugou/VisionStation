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

    /// <summary>工具名，须与配方中 ROI 的 Name 对应（"Defect"）</summary>
    public string Name => "Defect";

    /// <summary>
    /// 创建缺陷检测工具
    /// </summary>
    /// <param name="areaMin">最小缺陷面积（像素），默认 50</param>
    /// <param name="thresholdValue">二值化阈值，默认 128（金属亮背景/暗缺陷的常规工况）</param>
    public DefectTool(double areaMin = 50, double thresholdValue = 128)
    {
        AreaMin = areaMin;
        ThresholdValue = thresholdValue;
    }

    /// <summary>
    /// 执行表面缺陷检测
    ///
    /// 处理流水线（针对 ROI 内的灰度图）：
    ///   1. 裁剪 ROI
    ///   2. 高斯模糊（5x5）—— 抑制金属表面纹理和成像噪点，防止它们被误检为缺陷
    ///   3. 阈值二值化（BinaryInv）—— 金属件背景是亮的、缺陷（划痕/缺口/污渍）偏暗。
    ///      BinaryInv 把"暗于阈值"的像素变白，即缺陷区域变白、背景变黑
    ///   4. 形态学开运算（3x3 矩形核）—— 先腐蚀再膨胀，去掉孤立的小白点（残留噪点）
    ///   5. 连通域分析 + 三重过滤（面积 / 贴边 / 孔排除圆）
    ///   6. 在过滤后的掩码上找轮廓，按长宽比分类：细长=划痕，团块=缺口
    ///
    /// 坐标约定：算法都在 ROI 局部坐标系内进行，
    /// 输出标注前统一 +roi.X / +roi.Y 换算回原图坐标
    /// </summary>
    /// <param name="grayImage">整幅灰度图（Pipeline 已统一 CvtColor，此处只负责裁剪）</param>
    /// <param name="roi">本工具的检测区域（Name 匹配 "Defect"）</param>
    /// <returns>
    /// OK：表面无缺陷；NG：检出划痕/缺口，或 ROI 无效。
    /// Metrics：ScratchCount（划痕数）、NotchCount（缺口数）、TotalContours（过滤后轮廓总数）
    /// </returns>
    public ToolResult Execute(Mat grayImage, RoiRegion roi)
    {
        // 第1步：裁出 ROI 区域；区域越界/为空说明配方配置有问题，直接判 NG
        using var roiMat = roi.Crop(grayImage);
        if (roiMat.Empty())
            return ToolResult.Ng("ROI无效");

        // 第2步：高斯去噪 —— 5x5 核是尺寸与计算量的折中；sigma=0 由核尺寸自动推算
        using var blurred = new Mat();
        Cv2.GaussianBlur(roiMat, blurred, new Size(5, 5), 0);

        // 第3步：二值化（BinaryInv）—— 缺陷比金属底色暗，反相二值化后缺陷=白(255)、背景=黑(0)
        using var binary = new Mat();
        Cv2.Threshold(blurred, binary, ThresholdValue, 255, ThresholdTypes.BinaryInv);

        // 第4步：形态学开运算 —— 腐蚀掉小于核的白色噪点，再膨胀还原缺陷主体；
        // 效果 = "去掉比 3x3 还小的碎屑信号"，防止噪声被当成缺陷
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using var cleaned = new Mat();
        Cv2.MorphologyEx(binary, cleaned, MorphTypes.Open, kernel);

        // ══ 第5步：连通域过滤，只保留"真正的表面缺陷" ══
        // ConnectedComponentsWithStats 给每个白色连通域编号（0=背景），
        // stats 行结构：[Left, Top, Width, Height, Area]，centroids 行：[重心X, 重心Y]
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int compCount = Cv2.ConnectedComponentsWithStats(
            cleaned, labels, stats, centroids, PixelConnectivity.Connectivity8);

        // 排除圆三个坐标都合法才启用（默认 -1/0 = 不排除）
        bool exclusionEnabled = ExcludeRadius > 0 && ExcludeCenterX >= 0 && ExcludeCenterY >= 0;

        // validMask：过滤后"确认是缺陷"的像素集合，后面只在它上面找轮廓
        using var validMask = new Mat(cleaned.Rows, cleaned.Cols, MatType.CV_8UC1, Scalar.All(0));
        using var compMask = new Mat();

        // i 从 1 开始：0 号固定是背景，跳过
        int edgeSuppressed = 0;   // 被"贴边规则"丢掉的连通域数（F-C06）
        for (int i = 1; i < compCount; i++)
        {
            int left = stats.At<int>(i, (int)ConnectedComponentsTypes.Left);
            int top = stats.At<int>(i, (int)ConnectedComponentsTypes.Top);
            int width = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
            int height = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);

            // 过滤①：面积不足 —— 太小的要么是残余噪点，要么不构成可判定的缺陷
            if (area < AreaMin) continue;

            // 过滤②：剔除贴 ROI 边界的连通域 ——
            // 真实的表面缺陷是完整的、孤立的；贴边说明它是被 ROI 裁断的背景或边缘区域，
            // 形状不完整、判定不可靠，宁可漏判不可误判。
            // 但"漏判"必须留痕（F-C06）：统计被抑制的数量，让事后能看出
            // "这一帧其实有 N 个贴边信号被丢掉了"，而不是干净地报一句"表面无缺陷"。
            if (left <= 0 || top <= 0 ||
                left + width >= cleaned.Cols || top + height >= cleaned.Rows)
            {
                edgeSuppressed++;
                continue;
            }

            // 过滤③：剔除零件固有孔（如垫圈中心孔）——
            // 连通域重心是 ROI 局部坐标，+roi.X/Y 转成原图坐标后，
            // 与排除圆圆心算欧氏距离；落入排除圆 = 固有结构，不是缺陷
            if (exclusionEnabled)
            {
                double cx = centroids.At<double>(i, 0);
                double cy = centroids.At<double>(i, 1);
                double dist = Math.Sqrt(
                    Math.Pow(cx + roi.X - ExcludeCenterX, 2) +
                    Math.Pow(cy + roi.Y - ExcludeCenterY, 2));
                if (dist <= ExcludeRadius) continue;
            }

            // 三关都过了才算缺陷：InRange 把 labels 中等于 i 的像素抠出来，
            // 叠写进 validMask（255），等价于"按编号复制连通域"
            Cv2.InRange(labels, new Scalar(i), new Scalar(i), compMask);
            validMask.SetTo(new Scalar(255), compMask);
        }

        // 第6步：在过滤后的掩码上找轮廓 —— 只取外轮廓（External），缺陷不需要内孔信息
        Cv2.FindContours(validMask, out var contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        int scratchCount = 0, notchCount = 0;
        var overlays = new List<DrawCommand>();

        foreach (var contour in contours)
        {
            // 长宽比归一化：无论缺陷是横是竖，都取 w/h 与 h/w 中大的那个，
            // 保证"细长程度"的度量与方向无关
            var rect = Cv2.BoundingRect(contour);
            double aspectRatio = (double)rect.Width / rect.Height;
            if (aspectRatio < 1.0) aspectRatio = 1.0 / aspectRatio;

            // 轮廓点 → 原图坐标：轮廓在 ROI 局部系内，叠加 ROI 偏移还原到整幅图
            var points = contour.Select(p => new Point(p.X + roi.X, p.Y + roi.Y)).ToArray();

            // 分类：足够细长 = 划痕（拉伤/划伤）；否则归为缺口（崩边/啃伤）
            bool isScratch = aspectRatio >= ScratchAspectRatioMin;
            if (isScratch) scratchCount++; else notchCount++;

            overlays.Add(new DrawContour
            {
                Points = points,
                Color = isScratch
                    ? new Scalar(0, 0, 255)    // 红色=划痕（BGR）
                    : new Scalar(0, 165, 255),  // 橙色=缺口（BGR）
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
                ["TotalContours"] = contours.Length,
                ["EdgeSuppressed"] = edgeSuppressed
            },
            Summary = total == 0
                ? "表面无缺陷"
                : $"划痕:{scratchCount} 缺口:{notchCount} 总数:{total}",
            Overlays = overlays
        };
    }
}
