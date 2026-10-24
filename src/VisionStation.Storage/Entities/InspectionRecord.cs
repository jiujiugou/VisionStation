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

    /// <summary>
    /// 产出这条记录的引擎（"VisionMaster" / "OnnxYolo"）。
    ///
    /// 为什么单独存一列：换引擎之后最常被问的一句话是
    /// "这批料的判定是哪个引擎出的、换引擎前后有没有漂"。
    /// 只写日志答不了这个问题，日志会被管家清掉，而记录要留 30 天。
    /// </summary>
    [MaxLength(40)]
    public string EngineName { get; set; } = string.Empty;

    // ── 以下三组是「定位 / 测量 / 缺陷」的投影列，沿用旧列名 ──
    // 旧列名（Presence/Dimension/Defect）来自自研工具链时代，改名要重建表，
    // 而完整结论本来就在 ToolResultsJson 里 —— 不值得为改名做一次迁移。
    // 现在按 ToolResult.Role 投影：Presence* ← Locate，Dimension* ← Measure，Defect* ← Defect。

    // 定位 / 有无
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
