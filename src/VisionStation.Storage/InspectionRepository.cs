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
/// 换一种存储就得改界面。这里只有六个字段，够用且能单测。
/// </summary>
/// <param name="Since">起始时刻（含）。null = 不限</param>
/// <param name="Until">结束时刻（不含）。null = 到现在</param>
/// <param name="IsOk">true=只要 OK，false=只要 NG，null=全部</param>
/// <param name="EngineName">引擎名精确匹配。null/空 = 全部</param>
/// <param name="Take">最多返回多少条。上限由调用方控制（界面翻页/缩范围）</param>
/// <param name="Skip">跳过多少条（本轮不做翻页 UI，留给以后）</param>
public sealed record InspectionQuery(
    DateTime? Since = null,
    DateTime? Until = null,
    bool? IsOk = null,
    string? EngineName = null,
    int Take = 200,
    int Skip = 0);

/// <summary>
/// 检测记录数据访问契约。
/// 管家服务（清理线程）与检测管线（保存线程）会并发使用它，
/// 所以调用方只依赖接口：实现可以做到「每次操作一个 DbContext」。
/// </summary>
public interface IInspectionRepository
{
    Task EnsureCreatedAsync();
    Task SaveAsync(InspectionReport report);
    Task<List<InspectionRecord>> GetRecentAsync(int count = 100);
    Task<(int OkCount, int NgCount)> GetStatisticsAsync(DateTime? since = null);
    Task<int> DeleteOlderThanAsync(DateTime cutoff);

    /// <summary>按条件查历史（时间倒序）。界面历史页用。</summary>
    Task<List<InspectionRecord>> QueryAsync(InspectionQuery query);

    /// <summary>符合条件的总条数（用于"共 N 条"与"还有更多"判断）。</summary>
    Task<int> CountAsync(InspectionQuery query);

    /// <summary>
    /// 库里出现过的引擎名（去重）。给历史页的引擎下拉用 ——
    /// 写死 Onnx/Vm 会在换引擎那天变成错的下拉。
    /// </summary>
    Task<List<string>> ListEngineNamesAsync();
}

/// <summary>
/// 检测记录数据访问层 —— SQLite 的完整 CRUD
///
/// 不能注册全进程单例 InspectionDbContext：管家清理（Timer 线程）与检测保存（采集线程）
/// 会同时用它，而 EF Core 明确禁止同一 context 并发操作 —— SaveChanges 抛异常、记录直接丢。
///
/// 现在：持有 dbPath，每次操作新建并释放一个 DbContext（EF Core 的标准做法）。
/// 保留接受现成 DbContext 的构造，仅供单元测试与短生命周期场景使用。
/// </summary>
public class InspectionRepository : IInspectionRepository
{
    private readonly Func<(InspectionDbContext Db, bool Owned)> _newContext;
    private readonly ILogger<InspectionRepository>? _logger;

    // JsonSerializerOptions 每次 new 都会有反射元数据开销（CA1869），提到静态。
    private static readonly JsonSerializerOptions ToolJsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// 把全部工具结果压成 JSON 快照。
    /// 不存 Overlays：那是画标注用的图形指令（DrawRect/DrawCircle 等派生类型），
    /// 存下来也无法反序列化回具体类型，且体积远大于结论本身。
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

    /// <summary>生产用：每次操作独立 context，可并发调用</summary>
    public InspectionRepository(string dbPath, ILogger<InspectionRepository>? logger = null)
    {
        _newContext = () => (new InspectionDbContext(dbPath), true);
        _logger = logger;
    }

    /// <summary>测试/短生命周期用：复用传入的 context（调用方负责释放）</summary>
    public InspectionRepository(InspectionDbContext db, ILogger<InspectionRepository>? logger = null)
    {
        _newContext = () => (db, false);
        _logger = logger;
    }

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

    /// <summary>SQLite 错误码 5 = SQLITE_BUSY（另一个连接持锁）。</summary>
    private static bool IsDatabaseLocked(DbUpdateException ex)
        => ex.InnerException is SqliteException { SqliteErrorCode: 5 };

    /// <summary>确保数据库和表已创建（SQLite 文件不存在 → 自动创建）</summary>
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

    /// <summary>保存一次检测报告到数据库</summary>
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
            Id = report.Id,
            Timestamp = report.Timestamp,
            TriggerSource = report.TriggerSource,
            ImagePath = report.ImagePath,
            EngineName = report.Engine,

            // ── 定位 / 有无 ──
            PresenceOk = p.IsOk,
            PresenceDetail = p.Summary,
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

            CreatedAt = DateTime.Now
        };

        db.InspectionRecords.Add(record);
        await db.SaveChangesAsync().ConfigureAwait(false);
        _logger?.LogInformation("[DB] 保存记录 ID:{Id} 判定:{Verdict}", report.Id, report.OverallVerdict);
        return true;
    });

    /// <summary>查询最近 N 条记录（按时间倒序）</summary>
    public Task<List<InspectionRecord>> GetRecentAsync(int count = 100) => RunAsync(db =>
        db.InspectionRecords
            .OrderByDescending(r => r.CreatedAt)
            .Take(count)
            .ToListAsync());

    /// <summary>统计 OK/NG 数量（可选时间范围）</summary>
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

    /// <summary>按条件筛选（时间 / 判定 / 引擎），按检测时刻倒序。</summary>
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

    /// <summary>符合条件的总条数。</summary>
    public Task<int> CountAsync(InspectionQuery query) => RunAsync(async db =>
    {
        ArgumentNullException.ThrowIfNull(query);
        return await ApplyFilter(db.InspectionRecords.AsQueryable(), query)
            .CountAsync().ConfigureAwait(false);
    });

    /// <summary>库里出现过的引擎名（去重、字典序）。</summary>
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

        return source;
    }

    /// <summary>批量删除过期记录，返回删除条数（管家服务调用）</summary>
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
