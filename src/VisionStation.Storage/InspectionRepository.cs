using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VisionStation.Core.Models;
using VisionStation.Storage.Entities;

namespace VisionStation.Storage;

/// <summary>
/// 历史查询条件。**全部可选**，空对象等于"最近 200 条"。
///
/// 为什么不直接暴露 IQueryable 给界面：那样 EF 的查询构造就会长到 ViewModel 里，
/// 换一种存储就得改界面。这里只有七个字段，够用且能单测。
///
/// <para>
/// <b>这是取检测记录的唯一出口</b>：仓储里原先那个不带任何筛选的 <c>GetRecentAsync</c>
/// 已于 2026-10-25 删除 —— 它只有测试在调，测的还是一条生产永远不走的路径。
/// 现在"取最近 N 条"就是构造一个只填 <see cref="Take"/> 的本对象。
/// 不留一个"什么都不筛"的专用方法，是因为那等于给同一条查询开第二份实现，
/// 迟早和带筛选的那条在排序、上限、字段投影上跑偏，而界面只会在其中一条上被验证过。
/// </para>
/// </summary>
/// <param name="Since">起始时刻（含）。null = 不限</param>
/// <param name="Until">结束时刻（不含）。null = 到现在</param>
/// <param name="IsOk">true=只要 OK，false=只要 NG，null=全部</param>
/// <param name="EngineName">引擎名精确匹配。null/空 = 全部</param>
/// <param name="Sn">
/// 产品序列号精确匹配。null/空 = 全部。
///
/// <para>
/// <b>为什么是精确匹配而不是模糊</b>：条码是"这一件"的唯一标识，客户拿着实物来查时
/// 一定是一整串完整条码，不存在"记得一半"的场景。用 LIKE 反而会带来两个副作用 ——
/// 条码里的 <c>%</c> / <c>_</c> 需要转义（不转就是通配符），以及"输了一半就查到三条、
/// 不知道该看哪条"。要按前缀找的时候，让调用方自己拼，不要在这一层放宽语义。
/// </para>
/// </param>
/// <param name="Take">最多返回多少条。上限由调用方控制（历史页固定传 300，再多就该缩范围）</param>
/// <param name="Skip">跳过多少条。当前没有翻页 UI，生产调用方一律不填（默认 0）</param>
public sealed record InspectionQuery(
    DateTime? Since = null,
    DateTime? Until = null,
    bool? IsOk = null,
    string? EngineName = null,
    string? Sn = null,
    int Take = 200,
    int Skip = 0);

/// <summary>
/// 检测记录数据访问契约。
/// 管家服务（清理线程）与检测管线（保存线程）会并发使用它，
/// 所以调用方只依赖接口：实现可以做到「每次操作一个 DbContext」。
///
/// <para>
/// <b>谁在调（生产调用方就这些，没有别处）</b>：
/// <list type="bullet">
///   <item><see cref="SaveAsync"/> ← 检测链路 <c>InspectionWorkflow.AcceptExternalCoreAsync</c>（VM 推送那条路）；</item>
///   <item><see cref="EnsureCreatedAsync"/> ← <c>AppHost.InitializeAsync</c> 第 4 步（必须早于起 VM 监听）；</item>
///   <item><see cref="GetStatisticsAsync"/> ← <c>AppHost.InitializeAsync</c> 补今日良率基数，全生命周期就这一次；</item>
///   <item><see cref="DeleteOlderThanAsync"/> ← <c>HousekeepingService.CleanupOnceAsync</c>（Timer 线程，每小时一轮）；</item>
///   <item><see cref="QueryAsync"/> / <see cref="CountAsync"/> / <see cref="ListEngineNamesAsync"/> ← 历史页 <c>HistoryPageViewModel</c>。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>失败行为：抛，不把失败降级成 null / 空集合</b>。这里刻意不做"查不到就当空"的处理 ——
/// 空列表在界面上等于"这段时间没检测过"，和"库读不出来"是同一种画面、
/// 却是完全相反的结论。调用方各自决定怎么处理（管家记 Error 跳过本轮、历史页清空列表并写原因）。
/// 查询成功但真的没数据时，返回的是空列表而不是 null —— 调用方只需处理一种"空"。
/// </para>
///
/// <para>
/// <b>只增不改</b>：没有 Update / 单条 Delete。一条检测记录落库后就是既成事实，
/// 唯一的删除是按保留期的批量清理。想"修正"一条记录，正确做法是重新检测一帧，
/// 而不是改历史 —— 否则质量追溯就没有依据了。
/// </para>
/// </summary>
public interface IInspectionRepository
{
    Task EnsureCreatedAsync();
    Task SaveAsync(InspectionReport report);
    Task<(int OkCount, int NgCount)> GetStatisticsAsync(DateTime? since = null);
    Task<int> DeleteOlderThanAsync(DateTime cutoff);

    /// <summary>按条件查历史（时间倒序）。界面历史页用。</summary>
    Task<List<InspectionRecord>> QueryAsync(InspectionQuery query);

    /// <summary>符合条件的总条数（用于"共 N 条"与"还有更多"判断）。</summary>
    Task<int> CountAsync(InspectionQuery query);

    /// <summary>
    /// 库里出现过的引擎名（去重）。给历史页的引擎下拉用 ——
    /// 必须查库而不是写死常量，否则换引擎那天下拉就错了。
    /// </summary>
    Task<List<string>> ListEngineNamesAsync();
}

/// <summary>
/// 检测记录数据访问层 —— 写入（纯追加）+ 查询 + 统计 + 按时间批量删。
///
/// 不能注册全进程单例 InspectionDbContext：管家清理（Timer 线程）与检测保存（采集线程）
/// 会同时用它，而 EF Core 明确禁止同一 context 并发操作 —— SaveChanges 抛异常、记录直接丢。
///
/// 现在：持有 dbPath，每次操作新建并释放一个 DbContext（EF Core 的标准做法）。
/// 保留接受现成 DbContext 的构造，仅供单元测试与短生命周期场景使用。
///
/// <para>
/// <b>归属</b>：由 <c>ServiceCollectionExtensions.AddStorage</c> 注册为 DI 单例，活到容器释放。
/// 单例安全的前提是"本类自己不持有 DbContext"，只持有一个 <c>new DbContext</c> 的工厂委托 ——
/// 每次方法调用现开现关，所以管家线程与检测线程可以真正并发进来。
/// </para>
///
/// <para>
/// <b>线程约束</b>：实例本身无状态（两个字段都是只读），可被多线程同时调用；
/// 但**同一个 DbContext 实例**（测试构造那条路）不是线程安全的，那条路只能在单线程里用。
/// </para>
///
/// <para>
/// <b>失败行为</b>：异常一律向上抛。唯一的加工是把 SQLite 的 "database is locked"
/// 翻译成一句现场能照做的话（见 <see cref="RunAsync"/>），不吞、不降级成返回值。
/// </para>
/// </summary>
public class InspectionRepository : IInspectionRepository
{
    /// <summary>
    /// 取 context 的工厂。第二项 <c>Owned</c> 表示"这个 context 归本次调用所有，用完要释放"：
    /// 生产构造给 true，测试构造给 false —— 别人的 context 由别人释放。
    /// 弄错方向的后果不对称：该释放没释放会拖住 SQLite 的写锁（WAL 下 -wal 文件跟着涨），
    /// 不该释放却释放了则会让下一个用它的测试拿到已释放的对象。
    /// </summary>
    private readonly Func<(InspectionDbContext Db, bool Owned)> _newContext;

    /// <summary>日志。可空：单元测试构造时通常不传，仓储不该因为"没给 logger"就用不了。</summary>
    private readonly ILogger<InspectionRepository>? _logger;

    // JsonSerializerOptions 每次 new 都会有反射元数据开销（CA1869），提到静态。
    private static readonly JsonSerializerOptions ToolJsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// 把全部工具结果压成 JSON 快照。
    /// 不存 Overlays：那是画标注用的图形指令（DrawRect/DrawCircle 等派生类型），
    /// 存下来也无法反序列化回具体类型，且体积远大于结论本身。
    ///
    /// <para>
    /// 空/无结果显示成 <c>"{}"</c> 而不是 null 或空串：这一列在实体上是非空的，
    /// 且下游一律当 JSON 对象解析 —— 写空串等于让"这一帧没有任何结论"变成解析异常。
    /// </para>
    /// </summary>
    private static string SerializeToolResults(Dictionary<string, ToolResult>? results)
    {
        if (results is null || results.Count == 0) return "{}";

        var snapshot = new Dictionary<string, object>(results.Count, StringComparer.Ordinal);
        foreach (var kv in results)
        {
            snapshot[kv.Key] = new
            {
                kv.Value.IsOk,
                kv.Value.Summary,
                kv.Value.Metrics
            };
        }
        return JsonSerializer.Serialize(snapshot, ToolJsonOptions);
    }

    /// <summary>
    /// 生产用：每次操作独立 context，可并发调用。
    ///
    /// <para>
    /// 只吃 dbPath（<c>AppPaths.DbPath</c>，已解析成绝对路径），不吃现成 context ——
    /// 这样"谁 new 谁释放"没有歧义：每次调用自己开、自己 Dispose。
    /// 每帧新建一个 SqliteConnection 的开销在几十微秒量级，
    /// 换来的是一条不会因为别人正在用而阻塞的写入路径。
    /// </para>
    /// </summary>
    public InspectionRepository(string dbPath, ILogger<InspectionRepository>? logger = null)
    {
        _newContext = () => (new InspectionDbContext(dbPath), true);
        _logger = logger;
    }

    /// <summary>
    /// 测试/短生命周期用：复用传入的 context（调用方负责释放）。
    ///
    /// <para>
    /// <b>这条路不能并发</b>：同一个 <see cref="InspectionDbContext"/> 被两个线程同时用，
    /// EF 会抛"a second operation was started on this context"。
    /// 保留它只是为了让测试能用 SQLite 内存库/临时文件库一次建好 schema 反复查，
    /// 生产装配（<c>AddStorage</c>）永远不会走到这里。
    /// </para>
    /// </summary>
    public InspectionRepository(InspectionDbContext db, ILogger<InspectionRepository>? logger = null)
    {
        _newContext = () => (db, false);
        _logger = logger;
    }

    /// <summary>
    /// 所有方法的统一外壳：拿 context → 干活 → 只释放自己那份。
    ///
    /// <para>
    /// 三个约定：
    /// <list type="number">
    ///   <item>释放放在 <c>finally</c> 里 —— 干活抛异常时连接必须还回去，
    ///         否则一次失败就会把写锁留在那儿，后面每一帧都跟着卡；</item>
    ///   <item>只翻译 <see cref="DbUpdateException"/> 且错误码明确是 SQLITE_BUSY 的情况，
    ///         其它异常原样上抛（宁可日志里有一段英文堆栈，也不要吞掉真正的原因）；</item>
    ///   <item><c>ConfigureAwait(false)</c>：本层不碰任何 UI 对象，续体不必回到 UI 线程 ——
    ///         回到 UI 线程去开连接，正好会把界面卡在写入上。</item>
    /// </list>
    /// </para>
    /// </summary>
    private async Task<T> RunAsync<T>(Func<InspectionDbContext, Task<T>> work)
    {
        var (db, owned) = _newContext();
        try
        {
            return await work(db).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsDatabaseLocked(ex))
        {
            // SQLite 的 "database is locked" 冒上去是一串英文堆栈，操作员看不懂，
            // 现场只能看到"检测异常"四个字。这里翻译成一句能直接照着排查的话，
            // 并明确"本条结果没入库" —— 否则会以为记录已经存下了。
            throw new InvalidOperationException(
                "数据库被锁定，本条检测结果未入库（可能有另一个实例、后台清理或外部工具正在写同一个 db）", ex);
        }
        finally
        {
            if (owned) await db.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// SQLite 错误码 5 = SQLITE_BUSY（另一个连接持锁）。
    ///
    /// <para>
    /// 只认 <see cref="DbUpdateException"/>：写路径（SaveChanges / ExecuteSql）被 EF 包了一层，
    /// 而查询路径的 SqliteException 是直接抛出来的 —— 所以"查询时库被锁"不会走到这句翻译，
    /// 界面上看到的是原文。这是已知的不对称，不是漏写：查询失败由页面自己写"查询失败：…"，
    /// 把原文带上反而更容易定位是谁占了锁。
    /// </para>
    /// </summary>
    private static bool IsDatabaseLocked(DbUpdateException ex)
        => ex.InnerException is SqliteException { SqliteErrorCode: 5 };

    /// <summary>
    /// 确保数据库和表已创建（SQLite 文件不存在 → 自动创建）。
    ///
    /// <para>
    /// 幂等，且**每次启动都会跑**（<c>AppHost.InitializeAsync</c> 第 4 步，早于起 VM 监听）：
    /// 第一帧结果进来时表必须已经在。代价是启动时多一次 sqlite_master 查询，
    /// 换来的是"现场老库能自动补表补列"，而不是开发机好好的、装机第一条结果就抛 no such column。
    /// </para>
    /// <para>
    /// 失败直接抛（库文件被占用、目录不可写）—— 建不出表就写不进任何结果，
    /// 这种失败必须在启动期暴露，不能降级成"先跑着，记录丢一半"。
    /// </para>
    /// </summary>
    public Task EnsureCreatedAsync() => RunAsync(async db =>
    {
        // 不能只调 EnsureCreated：它对已存在的库既不加列也不建表，
        // 加一列会 "no such column"，加一张表会 "no such table"。
        var (journal, addedTables, addedColumns) =
            await StorageSchema.EnsureUpToDateAsync(db).ConfigureAwait(false);

        if (addedTables.Count > 0)
            _logger?.LogWarning("[DB] 结构升级：新建缺失表 {Tables}", string.Join(", ", addedTables));
        if (addedColumns.Count > 0)
            _logger?.LogWarning("[DB] 结构升级：补充缺失列 {Columns}", string.Join(", ", addedColumns));
        _logger?.LogInformation("[DB] 数据库初始化完成（journal={Journal}）", journal);
        return true;
    });

    /// <summary>
    /// 保存一次检测报告到数据库 —— 全程序写检测记录的唯一入口。
    ///
    /// <para>
    /// <b>事务边界</b>：只有一次 <c>SaveChangesAsync</c>，EF 会把它包成单个隐式事务，
    /// 所以一条记录要么整条进库、要么整条不进，不存在"半条记录"。
    /// 一帧就一条（<c>Id = report.Id</c>），没有跨表写入，因此不需要显式 BeginTransaction。
    /// </para>
    ///
    /// <para>
    /// <b>失败传播</b>：抛。调用方 <c>InspectionWorkflow.AcceptExternalCoreAsync</c> 里，
    /// 本方法在"回写 PLC"之前调用 —— 入库抛了，PLC 那一步就不会执行，
    /// 于是产线停在等结果、人能看到错误，而不是"PLC 放行了这件、库里什么都没有"。
    /// 再往上，<c>VmInspectionDriver</c> 捕获后记 Error 并继续收下一帧（不整条链路崩掉）。
    /// </para>
    ///
    /// <para>
    /// <b>代价</b>：这一帧的判定不会进库、也不会进今日良率。
    /// 这是有意的取舍 —— 宁可少一条记录，也不要一条记错位置或记错结论的记录。
    /// </para>
    /// </summary>
    public Task SaveAsync(InspectionReport report) => RunAsync(async db =>
    {
        // 旧列投影：按 ToolResult.Role 聚合（Locate / Measure / Defect）。
        // 报告里的键是业务名（"外径"、"表面缺陷"），由配方决定；
        // 角色是受约束的枚举，所以业务名怎么改都不会让这三列变成 0。
        var p = report.AggregateByRole(ResultKeys.Locate);
        var d = report.AggregateByRole(ResultKeys.Measure);
        var f = report.AggregateByRole(ResultKeys.Defect);

        var record = new InspectionRecord
        {
            // 主键直接用报告 ID，不另生成：同一份报告重存会主键冲突（抛出，不静默覆盖）——
            // 这条记录被写过两次说明上游重发了，值得看见，而不是悄悄盖掉第一次的结果。
            Id = report.Id,
            Timestamp = report.Timestamp,
            TriggerSource = report.TriggerSource,
            ImagePath = report.ImagePath,
            EngineName = report.Engine,
            Sn = report.Sn ?? string.Empty,

            // ── 定位 / 有无 ──
            PresenceOk = p.IsOk,
            PresenceDetail = p.Summary,
            // 指标缺失落 0：库里分不清"真的是 0"和"这次没给出这个指标"，
            // 要分辨只能回看 ToolResultsJson —— 投影列是给旧 UI 和快速筛选用的。
            PresenceScore = p.Metrics.GetValueOrDefault("MatchScore", 0),

            // ── 测量 ──
            DimensionOk = d.IsOk,
            DimensionDetail = d.Summary,
            DesignDiameter = d.Metrics.GetValueOrDefault("DesignedDiameter_mm", 0),
            MeasuredDiameter = d.Metrics.GetValueOrDefault("MeasuredDiameter_mm", 0),
            Deviation = d.Metrics.GetValueOrDefault("Deviation_mm", 0),

            // ── 缺陷检测 ──
            DefectOk = f.IsOk,
            DefectDetail = f.Summary,
            ScratchCount = (int)f.Metrics.GetValueOrDefault("ScratchCount", 0),
            NotchCount = (int)f.Metrics.GetValueOrDefault("NotchCount", 0),

            OverallVerdict = report.OverallVerdict,

            // 三组硬编码列只是旧 UI 的投影；真正的全集在这里。
            // 不加这一列的话，配方里第四个工具的结论根本不进库，
            // 而 OverallVerdict 却把它算进去 —— 出现「总判定 NG、三个子项全 OK」的不可解释记录。
            ToolResultsJson = SerializeToolResults(report.Results),

            // 入库时刻（本地时间，与库里其它时间字段同一个口径）。
            // 保留期清理删的是这一列，不是 Timestamp —— 见 DeleteOlderThanAsync 的说明。
            CreatedAt = DateTime.Now
        };

        db.InspectionRecords.Add(record);
        await db.SaveChangesAsync().ConfigureAwait(false);
        _logger?.LogInformation("[DB] 保存记录 ID:{Id} 判定:{Verdict}", report.Id, report.OverallVerdict);
        return true;
    });

    /// <summary>
    /// 统计 OK/NG 数量（可选时间范围）。
    ///
    /// <para>
    /// <b>这是唯一会做全表（或按 since 截断后的全表段）聚合的查询</b>，而且生产上只在
    /// <c>AppHost.InitializeAsync</c> 启动补基数时调一次。良率**不是**每帧来这里查的：
    /// 每帧一次聚合会带来两件坏事 —— ① 扫一遍当天全部记录，成本随班次线性增长
    /// （一天几万件时，每帧的界面更新都要先等一次全表扫描）；
    /// ② 那一帧本来就在写入，再叠一次读查询等于把两件事压在同一时刻，两边都变慢。
    /// 正确做法是启动时取一次基数（<c>ResultPanelViewModel.Seed</c>），之后在内存里累加。
    /// </para>
    ///
    /// <para>
    /// <b>关键判断</b>：分组后只认字面量 <c>"OK"</c> / <c>"NG"</c>，其它值（空串、拼错的大小写、
    /// 未来的第三态）两边都不计。所以 ok+ng 可能小于记录总数 ——
    /// 良率的分母按 ok+ng 算，那种记录不会把良率算歪，但也确实统计不到。
    /// 存判定时保持这两个大写字符串，是这条统计能对上的前提。
    /// </para>
    /// </summary>
    public Task<(int OkCount, int NgCount)> GetStatisticsAsync(DateTime? since = null) => RunAsync(async db =>
    {
        var query = db.InspectionRecords.AsQueryable();
        if (since.HasValue)
            query = query.Where(r => r.Timestamp >= since.Value);

        var groups = await query
            .GroupBy(r => r.OverallVerdict)
            .Select(g => new { Verdict = g.Key, Count = g.Count() })
            .ToListAsync().ConfigureAwait(false);

        int okCount = groups.FirstOrDefault(g => g.Verdict == "OK")?.Count ?? 0;
        int ngCount = groups.FirstOrDefault(g => g.Verdict == "NG")?.Count ?? 0;
        return (okCount, ngCount);
    });

    /// <summary>
    /// 按条件筛选（时间 / 判定 / 引擎），按检测时刻倒序。
    ///
    /// <para>
    /// 时间倒序是硬要求：历史页"最近的那一件"必须排第一，且 <c>Take</c> 截断时才截到最旧的尾巴。
    /// 排序用 <c>Timestamp</c>（判定时刻）而不是 <c>CreatedAt</c>（入库时刻）——
    /// VM 补传/重发时两者会有差，界面上该看到的是"这件什么时候检的"。
    /// 建表时 <c>Timestamp</c> 有索引，这个 OrderBy 不是全表排序。
    /// </para>
    /// </summary>
    public Task<List<InspectionRecord>> QueryAsync(InspectionQuery query) => RunAsync(async db =>
    {
        ArgumentNullException.ThrowIfNull(query);

        var filtered = ApplyFilter(db.InspectionRecords.AsQueryable(), query);
        return await filtered
            .OrderByDescending(r => r.Timestamp)
            .Skip(query.Skip)
            .Take(query.Take)
            .ToListAsync().ConfigureAwait(false);
    });

    /// <summary>
    /// 符合条件的总条数。
    ///
    /// <para>
    /// 与 <see cref="QueryAsync"/> 是两次独立的查询（不是同一个事务快照），中间可能插进新记录，
    /// 所以"列表 12 条 / 共 15 条"里那个 15 偶尔会比实际多一两条 —— 界面文案本身就是
    /// "共 N 条（还有更多）"，这种偏差不影响判断，比为此开一个事务划算。
    /// </para>
    /// <para>
    /// 注意 <c>Take</c> / <c>Skip</c> 不参与计数（<see cref="ApplyFilter"/> 只套筛选条件）：
    /// 界面上要的正是"筛选后一共多少"，被 300 条上限截断与否由调用方比较两个数字得知。
    /// </para>
    /// </summary>
    public Task<int> CountAsync(InspectionQuery query) => RunAsync(async db =>
    {
        ArgumentNullException.ThrowIfNull(query);
        return await ApplyFilter(db.InspectionRecords.AsQueryable(), query)
            .CountAsync().ConfigureAwait(false);
    });

    /// <summary>
    /// 库里出现过的引擎名（去重、字典序）。
    ///
    /// <para>
    /// 每次刷新历史页都会调一次（下拉的选项要跟着库走），走的是全表 DISTINCT ——
    /// <c>EngineName</c> 上没建索引，一天几十条的量级下这是笔可以忽略的开销。
    /// 如果哪天产量涨到"这步明显慢"，先加索引，不要改成写死几个名字：
    /// 写死的那天，换引擎的人已经不在了。
    /// </para>
    /// <para>
    /// 只排除空串：老记录（或将来某条没带引擎名的报文）不会污染下拉。
    /// </para>
    /// </summary>
    public Task<List<string>> ListEngineNamesAsync() => RunAsync(async db =>
        await db.InspectionRecords
            .Where(r => r.EngineName != "")
            .Select(r => r.EngineName)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync().ConfigureAwait(false));

    /// <summary>
    /// 把条件套到 IQueryable 上。
    ///
    /// 抽出来是**必需**的，不是整洁癖：Query 和 Count 必须用完全相同的筛选，
    /// 各写一份迟早出现"列表 12 条、底部写共 15 条"这种没法解释的界面。
    ///
    /// <para>
    /// 区间的开闭是有意的：<c>Since</c> 含、<c>Until</c> 不含。
    /// 界面按"今天/近 7 天"给起点、不给终点，所以这条只影响将来加"某天 0 点到次日 0 点"时
    /// 不会把两天交界的那一秒算两遍。
    /// </para>
    /// </summary>
    private static IQueryable<InspectionRecord> ApplyFilter(
        IQueryable<InspectionRecord> source, InspectionQuery query)
    {
        if (query.Since is { } since)
            source = source.Where(r => r.Timestamp >= since);

        if (query.Until is { } until)
            source = source.Where(r => r.Timestamp < until);

        if (query.IsOk is { } isOk)
        {
            // 比 OverallVerdict 字符串，而不是再加一个布尔列：
            // 那列会是第二份真相，早晚和 OverallVerdict 不同步。
            var verdict = isOk ? "OK" : "NG";
            source = source.Where(r => r.OverallVerdict == verdict);
        }

        if (!string.IsNullOrWhiteSpace(query.EngineName))
        {
            var engine = query.EngineName.Trim();
            source = source.Where(r => r.EngineName == engine);
        }

        // SN 也在这里 Trim：界面输入框里粘条码时常带空格/换行，
        // 不裁的话"看起来输入了却查不到"，而库里的值是当初 Trim 过的（见 VmResultParser.ToReport），
        // 两边不裁就会永久对不上。
        if (!string.IsNullOrWhiteSpace(query.Sn))
        {
            var sn = query.Sn.Trim();
            source = source.Where(r => r.Sn == sn);
        }

        return source;
    }

    /// <summary>
    /// 批量删除过期记录，返回删除条数（管家服务调用）。
    ///
    /// <para>
    /// <b>按 <c>CreatedAt</c>（入库时间）删，不是 <c>Timestamp</c>（判定时间）</b>：
    /// 保留期的语义是"这件事在我们库里躺了多久"，而且 CreatedAt 上有索引。
    /// 拿判定时间去删，遇上 VM 补传一帧昨天时间戳的结果，那条刚进库就会被当成过期删掉。
    /// </para>
    ///
    /// <para>
    /// 先 <c>ToListAsync</c> 再 <c>RemoveRange</c>：EF 的批量删要先有实体才删，同时也需要
    /// 准确条数回给日志（"这轮删了多少"是判断清理是否真的在跑的唯一依据）。
    /// 量级可控 —— 保留 30 天、一天几十条，一轮最多几千行；真涨到十万级再换 <c>ExecuteDelete</c>。
    /// </para>
    ///
    /// <para>
    /// 一个 <c>SaveChangesAsync</c> = 一个事务：删到一半失败就整轮回滚，
    /// 下个小时再试，不会留下"删了一半的库"。异常向上抛，由管家记 Error 并跳过本轮。
    /// </para>
    /// </summary>
    /// <param name="cutoff">此时间之前的记录全部删除</param>
    public Task<int> DeleteOlderThanAsync(DateTime cutoff) => RunAsync(async db =>
    {
        var old = await db.InspectionRecords
            .Where(r => r.CreatedAt < cutoff)
            .ToListAsync().ConfigureAwait(false);

        if (old.Count == 0) return 0;

        db.InspectionRecords.RemoveRange(old);
        await db.SaveChangesAsync().ConfigureAwait(false);
        _logger?.LogInformation("[DB] 删除 {Count} 条过期记录 (早于 {Cutoff:yyyy-MM-dd})",
            old.Count, cutoff);
        return old.Count;
    });
}
