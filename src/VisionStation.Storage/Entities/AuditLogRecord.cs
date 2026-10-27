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
///
/// <para>
/// <b>写入链路</b>：按钮命令 → <c>AuditService.Record(...)</c>（只入队，微秒级）
/// → 后台消费线程 → <c>AuditRepository.SaveAsync</c> → 本表。
/// 所以"用户点了按钮"和"这行落库"之间隔着一次队列，
/// 程序崩溃时最后几条审计可能丢在队列里（<c>StopAsync</c> 第 3.8 步有一次显式 Flush 来兜住正常关闭）。
/// </para>
///
/// <para>
/// <b>只增不改</b>：没有任何更新路径，唯一的删除是按时间整段过期（而且当前还没接上管家，
/// 见 <c>IAuditRepository.DeleteOlderThanAsync</c> 的说明）。这条约束是审计能被采信的前提。
/// </para>
///
/// <para>
/// <b>时间是本地时间</b>（<c>AuditService</c> 里写 <c>DateTime.Now</c>），
/// 与检测记录的 <c>Timestamp</c> 同口径；SQLite 以 TEXT 存、无时区偏移。
/// </para>
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

    /// <summary>
    /// 动作发生的本地时间（<c>DateTime.Now</c>，到毫秒）。
    /// 既是界面显示用的时间，也是保留期删除的判据 —— 本表没有单独的入库时间列，
    /// 因为审计的"发生"和"记录"本来就只差一次队列投递。
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// 操作人账号名。**是快照，不是外键**：账号停用/改名/删除都不影响历史记录，
    /// 反查"这是谁"要到用户表里按名字找，找不到说明这个人已经不在系统里了 —— 这本身也是信息。
    /// 未登录动作（启动、登录失败）用 Windows 账户名或 "unknown"。
    /// </summary>
    [MaxLength(64)]
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// 操作人角色（文本，不是枚举）。未登录时记"未登录"，身份取不到时记 "unknown"。
    /// 存文本而不是 <c>UserRole</c> 枚举：这是"当时是什么角色"的留痕，不该随着角色定义变化而改变含义。
    /// </summary>
    [MaxLength(24)]
    public string UserRole { get; set; } = string.Empty;

    /// <summary>
    /// 动作类型。EF 把枚举按 **int** 存 —— 所以枚举成员只能往后追加，不能删除、不能重排，
    /// 否则老记录的含义会跟着新代码一起变（这是这张表最容易被无意破坏的地方）。
    /// </summary>
    public AuditAction Action { get; set; }

    /// <summary>被操作对象的标识（配方名 / 账号名 / 页面名等）。空串表示这是个不带对象的动作。</summary>
    [MaxLength(120)]
    public string Target { get; set; } = string.Empty;

    /// <summary>
    /// 修改前的值（只对修改类动作有值）。
    /// 可空是因为"大多数审计根本没有前后值"（登录、启动、页面切换）——
    /// 写空串会让"没有值"和"值就是空"混在一起。
    /// </summary>
    [MaxLength(500)]
    public string? Before { get; set; }

    /// <summary>修改后的值。与 <see cref="Before"/> 成对，单边有值说明只记了一半。</summary>
    [MaxLength(500)]
    public string? After { get; set; }

    /// <summary>
    /// 结果。同样按 int 存枚举，同样只能追加不能重排（同 <see cref="Action"/> 的理由）。
    /// </summary>
    public AuditResult Result { get; set; }

    /// <summary>
    /// 人读的补充说明（"停用账号 operator02"、"登录失败：密码错误"这类）。
    /// 可空是因为不是每条审计都需要额外解释。
    /// </summary>
    [MaxLength(500)]
    public string? Detail { get; set; }
}
