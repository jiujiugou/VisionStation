using OpenCvSharp;
using VisionStation.Core.Models;

namespace VisionStation.Core.Tools;

/// <summary>
/// 有无检测工具 —— 模板匹配判断特征（圆孔）是否存在
///
/// 参数（构造注入）：
///   TemplatePath / TemplateMat — 模板图像
///   Threshold — 匹配度阈值，低于此值判 NG
/// </summary>
public class PresenceTool : IVisionTool
{
    private Mat? _template;
    private double _threshold;

    /// <summary>工具名，须与配方中 ROI 的 Name 对应（"Presence"）</summary>
    public string Name => "Presence";

    /// <summary>匹配度阈值（0~1），默认 0.70</summary>
    public double Threshold
    {
        get => _threshold;
        set => _threshold = Math.Clamp(value, 0.0, 1.0);
    }

    public PresenceTool(double threshold = 0.70)
    {
        Threshold = threshold;
    }

    /// <summary>从文件加载模板</summary>
    public void LoadTemplate(string path)
    {
        _template?.Dispose();
        _template = Cv2.ImRead(path, ImreadModes.Grayscale);
    }

    /// <summary>从 Mat 加载模板</summary>
    public void LoadTemplate(Mat mat)
    {
        _template?.Dispose();
        _template = mat.Clone();
    }

    /// <summary>
    /// 执行有无（特征存在性）检测
    ///
    /// 处理流水线（针对 ROI 内的灰度图）：
    ///   1. 前置校验：模板必须已加载、ROI 必须有效 —— 二者缺一无法检测，判 NG 并给出明确原因
    ///   2. 归一化相关系数模板匹配（CCoeffNormed）—— 在 ROI 内滑动模板，
    ///      每个位置得一个 [-1, 1] 的匹配分：1=完全一致，0=不相关，-1=负相关
    ///   3. MinMaxLoc 取最高分位置 —— 即"模板最可能出现在哪"
    ///   4. 最高分 ≥ 阈值 → 特征存在（OK）；否则 → 特征缺失（NG）
    ///
    /// 为什么选 CCoeffNormed 而不是 SQDIFF/CCORR：
    ///   相关系数对整体亮度偏移不敏感（产线光照轻微波动不误判），
    ///   且结果归一化到 [-1,1]，阈值语义稳定（0.7 就是"七分像"），跨产品可复用。
    ///
    /// 适用边界：模板匹配假设特征的方向和缩放基本不变 ——
    ///   本工具用于"位置大致固定的圆孔/特征存在性"判定，不做旋转/尺度自适应
    ///   （定位用 DefectTool 同款场景下的模板匹配定位属于另一工具的职责）
    /// </summary>
    /// <param name="grayImage">整幅灰度图（Pipeline 已统一 CvtColor）</param>
    /// <param name="roi">本工具的检测区域（Name 匹配 "Presence"）</param>
    /// <returns>
    /// OK：匹配度达标；NG：匹配度不足 / 模板未加载 / ROI 无效。
    /// Metrics：MatchScore（最高匹配度，4 位小数）
    /// </returns>
    public ToolResult Execute(Mat grayImage, RoiRegion roi)
    {
        // 前置校验①：模板未加载（配方里没配模板路径/加载失败）—— 无法检测，明确报因
        if (_template == null)
            return ToolResult.Ng("模板未加载");

        // 前置校验②：裁 ROI，越界/为空说明配方配置有问题
        using var roiMat = roi.Crop(grayImage);
        if (roiMat.Empty())
            return ToolResult.Ng("ROI无效");

        // 第2步：模板匹配 —— result 是"响应图"：
        // 尺寸 = (ROI宽-模板宽+1, ROI高-模板高+1)，每个像素代表模板左上角落在该处的匹配分
        using var result = new Mat();
        Cv2.MatchTemplate(roiMat, _template, result, TemplateMatchModes.CCoeffNormed);

        // 第3步：响应图的最大值 = 整个 ROI 内最像的位置的得分
        Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);

        // 第4步：阈值判定 —— Threshold 属性已 Clamp 到 [0,1]，不存在非法值
        var isOk = maxVal >= _threshold;
        var summary = isOk
            ? $"特征存在 (匹配度:{maxVal:F3})"
            : $"特征缺失 (匹配度:{maxVal:F3} < 阈值{_threshold})";

        // 标注：直接框出整个 ROI（有无检测关心的是"这片区域里有没有"，
        // 不像缺陷检测需要逐个轮廓标注）；绿=OK 红=NG（BGR），标签带匹配分
        return new ToolResult
        {
            IsOk = isOk,
            Summary = summary,
            Metrics = new() { ["MatchScore"] = Math.Round(maxVal, 4) },
            Overlays = new List<DrawCommand>
            {
                new DrawRect
                {
                    Rect = roi.ToRect(),
                    Color = isOk ? new Scalar(0, 180, 0) : new Scalar(0, 0, 220),
                    Label = $"{(isOk ? "OK" : "NG")} {maxVal:F2}",
                    Thickness = 2
                }
            }
        };
    }
}
