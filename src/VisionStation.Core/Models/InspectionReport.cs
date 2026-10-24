namespace VisionStation.Core.Models;

/// <summary>
/// 一次完整检测的报告 —— 一份可入库、可追溯、可解释的结论快照。
///
/// 数据结构：
///   Results 字典：Key = 结果角色或业务名（<see cref="ResultKeys"/> 或配方里的 label），
///                 Value = ToolResult
///   不用"三个固定字段"，因为引擎可换、模块数量不定；
///   硬编码字段会让 Report 随着 .sol 变化无限膨胀。
///
/// 判定语义（一条硬规则）：
///   IsOk = 至少有一项结果，且所有项都 OK。
///   空结果集必须判 NG —— Enumerable.All 对空集合返回 true，
///   直接写 All(...) 会让"引擎一项都没跑"的帧被判合格。没检 ≠ 合格。
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
    /// 产出本报告结论的引擎（"VisionMaster" / "OnnxYolo"）。
    /// 留着它是为了回答一个事后一定会被问到的问题：
    /// "这批料是哪个引擎判的、换引擎前后结论有没有漂"。
    /// </summary>
    public string Engine { get; set; } = string.Empty;

    /// <summary>所有检测项的结论。Key = 结果角色或业务名</summary>
    public Dictionary<string, ToolResult> Results { get; set; } = new();

    /// <summary>总体是否合格（非空且全部 OK）</summary>
    public bool IsOk => Results.Count > 0 && Results.Values.All(r => r.IsOk);

    /// <summary>总判定文字：OK 或 NG</summary>
    public string OverallVerdict => IsOk ? "OK" : "NG";

    /// <summary>存入一项结果</summary>
    public void SetToolResult(string name, ToolResult result)
        => Results[name] = result;

    /// <summary>获取一项结果（不存在返回 NG 占位，不抛异常）</summary>
    public ToolResult GetToolResult(string name)
        => Results.GetValueOrDefault(name,
            new ToolResult { IsOk = false, Summary = $"{name}: 未执行" });

    /// <summary>
    /// 把某个角色的所有结果聚合成一项（用于历史记录的旧列投影）。
    ///
    /// 聚合规则：只要有任意一项 NG 就是 NG（All 语义），摘要拼接。
    /// 没有该角色的项时返回「未执行」NG —— 与 <see cref="GetToolResult"/> 一致，
    /// 不能返回"空且 OK"，那会让"这项根本没检"看起来像"检了没问题"。
    /// </summary>
    public ToolResult AggregateByRole(string role)
    {
        var items = Results.Values.Where(r => r.Role == role).ToList();
        if (items.Count == 0)
            return new ToolResult { IsOk = false, Role = role, Summary = $"{role}: 未执行" };

        if (items.Count == 1) return items[0];

        return new ToolResult
        {
            IsOk = items.All(r => r.IsOk),
            Role = role,
            Summary = string.Join("；", items.Select(r => r.Summary)),
            Metrics = items.SelectMany(r => r.Metrics)
                .GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key, g => g.Last().Value),
            Overlays = items.SelectMany(r => r.Overlays).ToList()
        };
    }

    /// <summary>
    /// 造一份"引擎没能出结论"的报告（NG，且只有一项）。
    ///
    /// 为什么统一在 Core 里造：两个引擎的失败语义必须一致 ——
    /// 都是 NG、都带明确原因、都占用 <see cref="ResultKeys.Engine"/> 这个键。
    /// 各写一份迟早会漂（一个 NG 一个抛异常），而现场只看得出"有时报错有时报 NG"。
    /// </summary>
    /// <param name="engine">引擎名</param>
    /// <param name="reason">给人看的原因（会直接出现在界面日志里）</param>
    /// <param name="triggerSource">触发来源</param>
    /// <param name="imagePath">原图路径</param>
    public static InspectionReport Failure(string engine, string reason,
        string triggerSource = "Manual", string imagePath = "")
    {
        var report = new InspectionReport
        {
            Timestamp = DateTime.Now,
            TriggerSource = triggerSource,
            ImagePath = imagePath,
            Engine = engine
        };
        report.SetToolResult(ResultKeys.Engine, ToolResult.Ng(reason));
        return report;
    }
}
