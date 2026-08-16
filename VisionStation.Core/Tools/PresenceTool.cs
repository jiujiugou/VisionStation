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

    public ToolResult Execute(Mat grayImage, RoiRegion roi)
    {
        if (_template == null)
            return ToolResult.Ng("模板未加载");

        using var roiMat = roi.Crop(grayImage);
        if (roiMat.Empty())
            return ToolResult.Ng("ROI无效");

        using var result = new Mat();
        Cv2.MatchTemplate(roiMat, _template, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);

        var isOk = maxVal >= _threshold;
        var summary = isOk
            ? $"特征存在 (匹配度:{maxVal:F3})"
            : $"特征缺失 (匹配度:{maxVal:F3} < 阈值{_threshold})";

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
