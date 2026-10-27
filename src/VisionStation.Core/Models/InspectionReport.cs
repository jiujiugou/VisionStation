namespace VisionStation.Core.Models;

/// <summary>
/// 一次完整检测的报告 —— 一份可入库、可追溯、可解释的结论快照。
///
/// <para>
/// <b>定位</b>：这是链路的"中间凭证"。上游是 <c>VmResultParser</c>（把 VM 推来的一行报文
/// 翻译成它），下游是 <c>InspectionWorkflow.AcceptExternalCoreAsync</c>
/// （读图 → 标注 → 入库 → 回 PLC）与 <c>InspectionRepository.SaveAsync</c>（投影成记录行）。
/// 它不认识数据库、不认识界面、也不认识 VM —— 只是把"这一件被判成了什么"装齐，
/// 让上下游各自去用。
/// </para>
///
/// <para>
/// <b>归属</b>：每帧 new 一个，活一次处理就走完，不缓存、不跨帧复用。
/// 构造线程 = 收包线程（<c>VmInspectionDriver.OnLine</c>）；
/// 之后被 <c>Task.Run</c> 搬到线程池里读图、标注、入库；
/// 最后同一个实例随 <c>InspectionRunResult</c> 被发到 UI 线程上屏（此后只读）。
/// 也就是说它<b>不是线程安全的，但也不需要</b>：整条链上一次只有一个线程在动它。
/// 唯一会被下游改写的字段是 <see cref="ImagePath"/>，见该字段说明。
/// </para>
///
/// <para>
/// <b>失败行为</b>：本类不抛异常（没有 IO、没有解析）。"引擎没能出结论"由
/// <see cref="Failure"/> 造一份 NG 报告来表达，而不是抛给调用方 ——
/// 抛出去不会让程序崩，但这一帧会变成一条 Error 日志、既不判也不入库
/// （<c>VmInspectionDriver.OnLine</c> 的 catch 就是这么收的），
/// 现场表现为"这件产品凭空消失了"，比一条 NG 难查得多。
/// </para>
///
/// <para>
/// <b>数据结构</b>：<see cref="Results"/> 字典的 Key = 结果角色或业务名
/// （<see cref="ResultKeys"/> 或配方里的 label），Value = <see cref="ToolResult"/>。
/// 不用"三个固定字段"，因为引擎可换、模块数量不定；硬编码字段会让 Report
/// 随着 .sol 变化无限膨胀。旧的三列（定位/测量/缺陷）由 <see cref="AggregateByRole"/>
/// 现算投影，业务名怎么改都不会让它们变成 0。
/// </para>
///
/// <para>
/// <b>判定语义（一条硬规则）</b>：<see cref="IsOk"/> = 至少有一项结果，且所有项都 OK。
/// 空结果集必须判 NG —— <c>Enumerable.All</c> 对空集合返回 true，
/// 直接写 All(...) 会让"引擎一项都没跑"的帧被判合格。没检 ≠ 合格。
/// </para>
/// </summary>
public class InspectionReport
{
    /// <summary>
    /// 报告唯一 ID（GUID）。
    ///
    /// <b>它同时是数据库主键</b>（<c>InspectionRecord.Id</c> 直接取这个值），
    /// 所以同一份报告不能存两次 —— 重复保存会撞主键，而不是"多一条记录"。
    /// 用 GUID 而不是自增，是为了让"一条结果在日志里的 ID"和"库里的 ID"是同一个，
    /// 排查时能直接对上，不必再查一次库。
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 检测时间。JSON 报文里带了 VM 的时间就用 VM 的，没带就是本机收到的时间
    /// （<c>VmResultParser.ToReport</c> 里那句 <c>?? DateTime.Now</c>）。
    ///
    /// <b>它不等于"入库时间"</b>：保留期清理按入库时间算，不是按这个字段
    /// （见 <c>InspectionRepository.DeleteOlderThanAsync</c>）—— 否则一台机器时钟慢几小时，
    /// 刚写进来的记录就会被当成过期数据删掉。
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// 触发来源标记：Manual / Timer / PLC，VM 推送路径上记 "VM"。
    /// 报文没写来源时由解析器的 <c>triggerSourceFallback</c> 兜底，不会留下空串 ——
    /// 空串会让"这班是 PLC 触发还是人点的"这个问题永远无法回答。
    /// </summary>
    public string TriggerSource { get; set; } = string.Empty;

    /// <summary>
    /// 产品序列号 / 条码（VM 侧扫码模块给的，追溯用）。**允许为空**。
    ///
    /// <para>
    /// <b>为什么要有它</b>：没有 SN 时，"这一件 NG" 只能定位到某个时间点，
    /// 客户拿着实物回来问"这件为什么放行了"，只能靠时间猜。
    /// 有了 SN，历史页可以直接按条码查到那一条，这是接 MES 追溯的最小前提。
    /// </para>
    /// <para>
    /// <b>它不参与任何判定</b>：只存不用。空串和"没扫码"是同一件事，
    /// 所以不区分 null 与空串 —— 下游拼 SQL、拼日志的地方少一套分支。
    /// </para>
    /// <para>
    /// <b>只有 JSON 报文能带</b>：分隔符格式没有字段名，值按 <c>vm.outputs</c> 位置落位，
    /// 没有给 SN 留槽位（硬塞一个会让后面所有值错位）。所以要用 SN 就得走 JSON 格式。
    /// </para>
    /// </summary>
    public string Sn { get; set; } = string.Empty;

    /// <summary>
    /// 被检测的原图路径。**允许为空**：VM 没配存图、或只发了目录时就是空的。
    ///
    /// <para>
    /// 字段语义随相机层删除已经变了：以前是"本程序采的图存在哪"，
    /// 现在是"**VM 那边**存的图在哪"——本程序只读不写。
    /// </para>
    /// <para>
    /// <b>唯一一处会被下游改写的字段</b>：报文路径不可用时，工作流会用界面配置的
    /// 兜底目录找一张图，并把找到的绝对路径写回这里（见 <c>AcceptExternalCoreAsync</c>）。
    /// 不写回就会出现"界面显示的是兜底图、库里记的却是那个无效路径"，
    /// 事后点开历史记录看到的是另一件产品。
    /// </para>
    /// <para>
    /// 为空不影响入库：结论本来就该和"有没有图"解耦 —— 少一张图是次要问题，
    /// 少一条判定记录才是质量事故。
    /// </para>
    /// </summary>
    public string ImagePath { get; set; } = string.Empty;

    /// <summary>
    /// 产出本报告结论的引擎（运行时只有 "VisionMaster"）。
    /// 留着它是为了回答一个事后一定会被问到的问题：
    /// "这批料是哪个引擎判的、换引擎前后结论有没有漂"。
    /// 旧库里可能还留着早期引擎名（历史记录不该因为换了引擎就变成"数据损坏"），
    /// 读旧记录时不要把不认识的值当成错误。
    /// </summary>
    public string Engine { get; set; } = string.Empty;

    /// <summary>
    /// 所有检测项的结论。Key = 结果角色或业务名。
    ///
    /// 允许为空字典，但**空字典 = NG**（见 <see cref="IsOk"/>）。
    /// 键不能重复：同名项后写覆盖先写（<see cref="SetToolResult"/> 是索引赋值），
    /// 配方里 label 重名会被 <c>RecipeConfig.Validate</c> 拦下来，正是因为这个。
    /// </summary>
    public Dictionary<string, ToolResult> Results { get; set; } = new();

    /// <summary>
    /// 总体是否合格（非空且全部 OK）。这里刻意写成 <c>Count &gt; 0 &amp;&amp; All(...)</c>，
    /// 而不是只写 <c>All(...)</c>：空集合上 All 恒为 true，一行之差就是"什么都没测"被判合格。
    /// </summary>
    public bool IsOk => Results.Count > 0 && Results.Values.All(r => r.IsOk);

    /// <summary>总判定文字：OK 或 NG（数据库按这两个字符串分组统计良率，不要本地化它）</summary>
    public string OverallVerdict => IsOk ? "OK" : "NG";

    /// <summary>存入一项结果。同名覆盖 —— 静默覆盖，不报错，所以重名要靠配方校验提前拦住。</summary>
    public void SetToolResult(string name, ToolResult result)
        => Results[name] = result;

    /// <summary>
    /// 获取一项结果（不存在返回 NG 占位，不抛异常）。
    ///
    /// <para>
    /// 占位对象**不会**被写进 <see cref="Results"/> —— 只是给调用方一个能安全读
    /// <c>IsOk</c> / <c>Summary</c> 的东西。这点很重要：若把占位也塞进字典，
    /// "查询"就会变成"篡改"，一份原本空结果的报告会凭空多出一项 NG。
    /// </para>
    /// </summary>
    public ToolResult GetToolResult(string name)
        => Results.GetValueOrDefault(name,
            new ToolResult { IsOk = false, Summary = $"{name}: 未执行" });

    /// <summary>
    /// 把某个角色的所有结果聚合成一项（用于历史记录的旧列投影）。
    ///
    /// <para>
    /// 聚合规则：只要有任意一项 NG 就是 NG（All 语义），摘要拼接；
    /// 同名 Metric 只保留一条（<c>g.Last()</c>）。注意这个"最后"依赖
    /// <see cref="Results"/> 当前的枚举顺序（现实中等于插入顺序，但 Dictionary
    /// 不保证这一点），所以<b>不要让两个同角色的项写同一个 Metric 键</b> ——
    /// 那样聚合结果会随枚举顺序漂。
    /// </para>
    /// <para>
    /// 没有该角色的项时返回「未执行」NG —— 与 <see cref="GetToolResult"/> 一致，
    /// 不能返回"空且 OK"，那会让"这项根本没检"看起来像"检了没问题"。
    /// 这条在 VM 模式下尤其要紧：分隔符格式的每项都跟随总判定，
    /// 一旦角色没配，对应的旧列就应该是 NG（未执行），而不是悄悄给个 OK。
    /// </para>
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
    /// 造一份"引擎没能出结论"的报告（NG，且只有一项，键是 <see cref="ResultKeys.Engine"/>）。
    ///
    /// <para>
    /// <b>为什么统一在 Core 里造</b>：失败语义必须一致 —— 都是 NG、都带明确原因、
    /// 都占用 <see cref="ResultKeys.Engine"/> 这个键。各写一份迟早会漂
    /// （一个 NG 一个抛异常），而现场只看得出"有时报错有时报 NG"。
    /// </para>
    /// <para>
    /// <b>调用方</b>：<c>VmResultParser</c> 的每一条失败出口，以及 <c>VmExternalEngine.Inspect</c>
    /// （人工误触主动检测时）。它<b>不抛异常</b>，所以可以直接放在收包线程上调用。
    /// </para>
    /// <para>
    /// <b>副作用</b>：无。返回的是普通对象，是否入库由调用方决定 ——
    /// 这也是为什么失败报告一旦入库就会污染良率，工作流必须区分"失败"与"判定"。
    /// </para>
    /// </summary>
    /// <param name="engine">引擎名</param>
    /// <param name="reason">给人看的原因（会直接出现在界面日志里）</param>
    /// <param name="triggerSource">触发来源</param>
    /// <param name="imagePath">原图路径</param>
    /// <param name="sn">
    /// 产品序列号（可选）。失败报告也要能带 SN：报文里给了 SN、只是没给判定时，
    /// "哪一件没判出来"比"有一件没判出来"有用得多 —— 操作员可以照着 SN 去补检。
    /// </param>
    public static InspectionReport Failure(string engine, string reason,
        string triggerSource = "Manual", string imagePath = "", string sn = "")
    {
        var report = new InspectionReport
        {
            Timestamp = DateTime.Now,
            TriggerSource = triggerSource,
            ImagePath = imagePath,
            Sn = sn,
            Engine = engine
        };
        report.SetToolResult(ResultKeys.Engine, ToolResult.Ng(reason));
        return report;
    }
}
