using VisionStation.Core.Tools;

namespace VisionStation.Core.Models;

/// <summary>
/// 一次完整检测的报告 —— 聚合所有 Tool 的执行结果
///
/// 数据结构设计：
///   - Results 字典：Key=工具名（"Presence"/"Dimension"/"Defect"），Value=ToolResult
///   - 不硬编码三个字段（老版有 PresenceResult/DimensionResult/DefectResult），
///     因为将来可能加 OCR、二维码等新工具，硬编码字段会导致 Report 类无限膨胀
///   - 旧字段保留为兼容属性（内部映射到 Results 字典），避免大规模重构
///
/// 判定逻辑：
///   IsOk = 所有 Tool 的 IsOk 都为 true（All 语义——一项不合格整件不合格）
/// </summary>
public class InspectionReport
{
    /// <summary>报告唯一 ID（GUID）</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>检测时间</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>触发来源标记：Manual / Timer / PLC</summary>
    public string TriggerSource { get; set; } = string.Empty;

    /// <summary>被检测的原图路径（FileCamera 下有意义）</summary>
    public string ImagePath { get; set; } = string.Empty;

    /// <summary>
    /// 所有工具的执行结果。
    /// Key = 工具名（"Presence" / "Dimension" / "Defect"）
    /// Value = ToolResult（IsOk + Summary + Metrics + DrawCommands）
    /// </summary>
    public Dictionary<string, ToolResult> Results { get; set; } = new();

    /// <summary>
    /// 总体是否合格（所有 Tool 都 OK 才 OK）
    ///
    /// 注意：空结果集必须判 NG（F-C01）。Enumerable.All 对空集合返回 true，
    /// 直接写 All(...) 会让「一个工具都没跑」的帧被判合格 —— 没检 ≠ 合格。
    /// </summary>
    public bool IsOk => Results.Count > 0 && Results.Values.All(r => r.IsOk);

    /// <summary>总判定文字：OK 或 NG</summary>
    public string OverallVerdict => IsOk ? "OK" : "NG";

    /// <summary>存入一个工具的结果</summary>
    public void SetToolResult(string name, ToolResult result)
        => Results[name] = result;

    /// <summary>获取一个工具的结果（不存在返回空结果，不抛异常）</summary>
    public ToolResult GetToolResult(string name)
        => Results.GetValueOrDefault(name,
            new ToolResult { IsOk = false, Summary = $"{name}: 未执行" });

    // ── 兼容属性（旧代码不用改，内部映射到字典）──
    public ToolResult PresenceResult => GetToolResult("Presence");
    public ToolResult DimensionResult => GetToolResult("Dimension");
    public ToolResult DefectResult => GetToolResult("Defect");
}
