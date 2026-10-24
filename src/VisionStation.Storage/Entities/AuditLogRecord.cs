using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using VisionStation.Core.Models;

namespace VisionStation.Storage.Entities;

/// <summary>
/// 审计记录 — SQLite 持久化实体。
///
/// 与 <see cref="InspectionRecord"/> 的区别：检测记录回答"这一件判得对不对"，
/// 审计记录回答"谁在什么时候动了什么"。两张表不合并——它们的量级、保留期、
/// 查询方式都不一样，混在一起以后想按人查都费劲。
/// </summary>
[Table("AuditLogs")]
public class AuditLogRecord
{
    /// <summary>
    /// 自增主键（不是 Guid）。
    ///
    /// 审计是纯追加流，天然有顺序，自增 ID 本身就是"第几条"，
    /// 而且 8 字节比 Guid 的 16 字节省一半——这张表是按年攒的。
    /// </summary>
    [Key]
    public long Id { get; set; }

    public DateTime Timestamp { get; set; }

    [MaxLength(64)]
    public string UserName { get; set; } = string.Empty;

    [MaxLength(24)]
    public string UserRole { get; set; } = string.Empty;

    public AuditAction Action { get; set; }

    [MaxLength(120)]
    public string Target { get; set; } = string.Empty;

    /// <summary>修改前的值（只对修改类动作有值）</summary>
    [MaxLength(500)]
    public string? Before { get; set; }

    /// <summary>修改后的值</summary>
    [MaxLength(500)]
    public string? After { get; set; }

    public AuditResult Result { get; set; }

    [MaxLength(500)]
    public string? Detail { get; set; }
}
