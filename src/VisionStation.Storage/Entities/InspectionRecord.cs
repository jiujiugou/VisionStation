using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VisionStation.Storage.Entities;

/// <summary>
/// 检测记录 — SQLite 持久化实体。
///
/// <para>
/// <b>一行 = 一帧结果的快照</b>：判定、各工具结论的摘要与关键数值、证据图路径。
/// 唯一的写入方是 <c>InspectionRepository.SaveAsync</c>（<c>InspectionWorkflow</c> 收到 VM 推送后调），
/// 之后**不再修改**。唯一的删除是管家按保留期的整段清理。
/// </para>
///
/// <para>
/// <b>时间字段都是本地时间</b>（<c>DateTime.Now</c>；VM 报文里的时间在
/// <c>VmResultParser</c> 用 <c>LocalDateTime</c> 转成本地），不是 UTC。
/// SQLite 里以 TEXT 存、不带时区偏移，所以跨时区拷库没有意义，
/// 在别的机器上打开看到的时间也不会被"换算"—— 这正是一个单机工作站想要的行为。
/// </para>
///
/// <para>
/// <b>判定字段的编码</b>：总判定是字符串 <c>"OK"</c> / <c>"NG"</c>（不是 bool、也不是 1/2），
/// 三组子项是 <c>bool</c> 且 **true = 合格**。统计与筛选都按字符串字面量比较，
/// 写入时保持这两个大写值，是良率能对上的前提。
/// </para>
///
/// <para>
/// <b>可空性</b>：本表没有可空列 —— 字符串一律默认空串、数值无量时落 0、
/// <c>ToolResultsJson</c> 默认 <c>"{}"</c>。这样查询侧不用到处判 null，
/// 代价是"0"和"没测到"在数值列上分不开（要分辨只能看 JSON）。
/// </para>
/// </summary>
[Table("InspectionRecords")]
public class InspectionRecord
{
    /// <summary>
    /// 主键＝报告 ID（由上游生成，不是库自增）。同一份报告重存会撞主键并抛异常 ——
    /// 这是有意的：重发说明上游出过问题，值得被看见，而不是让它静默覆盖第一次的结果。
    /// </summary>
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 判定时刻（本地时间，含毫秒）。历史页按它倒序排、按它做时间范围筛选。
    /// 不保证单调：VM 补传时可能插进一条更早的时间戳，所以"最新的一条"以入库顺序看不一定在末尾。
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// 触发来源。生产上现在只有 <c>"VM"</c>（VM 报文带的，缺省时解析器回落到 VM）；
    /// <c>"Manual"</c> / <c>"Timer"</c> / <c>"PLC"</c> 是相机层还在时的历史取值。
    /// </summary>
    [MaxLength(50)]
    public string TriggerSource { get; set; } = string.Empty;  // "Manual" / "Timer" / "PLC"

    /// <summary>
    /// 证据图路径。**现在指向 VM 的输出目录**（本程序不存图，只把报文给的路径记下来），
    /// 所以这个文件不归我们管：可能被 VM 自己的保留策略删掉。
    /// 历史页因此把它当"可能失效的提示"处理 —— 选中时再判文件在不在，而不是当保证。
    /// </summary>
    [MaxLength(500)]
    public string ImagePath { get; set; } = string.Empty;

    /// <summary>
    /// 产出这条记录的引擎（当前只有 "VisionMaster"）。
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
    /// <summary>匹配分（模板匹配类工具才有）。0 既可能是"真的 0 分"也可能是"这帧没给分"。</summary>
    public double PresenceScore { get; set; }

    // 尺寸检测
    public bool DimensionOk { get; set; }
    public string DimensionDetail { get; set; } = string.Empty;
    /// <summary>设计值 / 实测值 / 偏差，单位 mm。三个都来自测量工具输出的指标键。</summary>
    public double DesignDiameter { get; set; }
    public double MeasuredDiameter { get; set; }
    public double Deviation { get; set; }

    // 缺陷检测
    public bool DefectOk { get; set; }
    public string DefectDetail { get; set; } = string.Empty;
    /// <summary>划痕数 / 缺口数。类型是 int，写入时把 double 指标强转过来（小数被截断）。</summary>
    public int ScratchCount { get; set; }
    public int NotchCount { get; set; }

    // 总判定
    /// <summary>
    /// 总判定，只允许 <c>"OK"</c> / <c>"NG"</c> 两个值（长度上限 10 只是留余量）。
    /// 良率统计按这个字符串分组，写成 "Ok" / "ok" / "PASS" 会让这条记录**两边都不计**。
    /// </summary>
    [MaxLength(10)]
    public string OverallVerdict { get; set; } = string.Empty;   // "OK" | "NG"

    /// <summary>
    /// 全部工具结果的快照（JSON）。三组硬编码列只保留给旧 UI 读，
    /// 真正的「这次检测都有哪些结论」在这里 —— 否则加第四个工具时，
    /// 它的 NG 会让 OverallVerdict=NG，但库里三个子项全 OK，复盘无法解释 NG 从哪来。
    /// </summary>
    public string ToolResultsJson { get; set; } = "{}";

    /// <summary>
    /// 入库时刻（本地时间，写入方给 <c>DateTime.Now</c>）。
    ///
    /// <para>
    /// 和 <see cref="Timestamp"/> 是两件事：前者是"这条记录什么时候进我们的库"，
    /// 后者是"这件产品什么时候被判的"。正常只差几毫秒；VM 补传/重发时会差很多。
    /// <b>管家按这一列删过期记录</b> —— 否则一条今天才入库、时间戳是昨天的补传记录会被立刻删掉。
    /// </para>
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
