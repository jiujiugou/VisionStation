namespace VisionStation.Core.Models;

/// <summary>
/// 单项检测结果（有无 / 尺寸 / 缺陷各一个）
/// </summary>
public class DetectionResult
{
    /// <summary>检测器名称: "Presence" / "Dimension" / "Defect"</summary>
    public string DetectorName { get; set; } = string.Empty;

    /// <summary>本项检测是否合格</summary>
    public bool IsOk { get; set; }

    /// <summary>人类可读的检测描述</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>具体测量指标</summary>
    public Dictionary<string, double> Metrics { get; set; } = new();

    public static DetectionResult Ok(string name, string detail, Dictionary<string, double>? metrics = null)
        => new()
        {
            DetectorName = name,
            IsOk = true,
            Detail = detail,
            Metrics = metrics ?? new()
        };

    public static DetectionResult Ng(string name, string detail, Dictionary<string, double>? metrics = null)
        => new()
        {
            DetectorName = name,
            IsOk = false,
            Detail = detail,
            Metrics = metrics ?? new()
        };
}
