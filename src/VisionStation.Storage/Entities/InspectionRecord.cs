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

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
