using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VisionStation.Storage.Entities;

/// <summary>
/// 检测记录 — SQLite 持久化实体
/// </summary>
[Table("InspectionRecords")]
public class InspectionRecord
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime Timestamp { get; set; }

    [MaxLength(50)]
    public string TriggerSource { get; set; } = string.Empty;  // "Manual" / "Timer" / "PLC"

    [MaxLength(500)]
    public string ImagePath { get; set; } = string.Empty;

    // 有无检测
    public bool PresenceOk { get; set; }
    public string PresenceDetail { get; set; } = string.Empty;
    public double PresenceScore { get; set; }

    // 尺寸检测
    public bool DimensionOk { get; set; }
    public string DimensionDetail { get; set; } = string.Empty;
    public double DesignDiameter { get; set; }
    public double MeasuredDiameter { get; set; }
    public double Deviation { get; set; }

    // 缺陷检测
    public bool DefectOk { get; set; }
    public string DefectDetail { get; set; } = string.Empty;
    public int ScratchCount { get; set; }
    public int NotchCount { get; set; }

    // 总判定
    [MaxLength(10)]
    public string OverallVerdict { get; set; } = string.Empty;   // "OK" | "NG"

    /// <summary>
    /// 全部工具结果的快照（JSON）。三组硬编码列只保留给旧 UI 读，
    /// 真正的「这次检测都有哪些结论」在这里 —— 否则加第四个工具时，
    /// 它的 NG 会让 OverallVerdict=NG，但库里三个子项全 OK，复盘无法解释 NG 从哪来。
    /// </summary>
    public string ToolResultsJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
