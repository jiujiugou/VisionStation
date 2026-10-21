using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;
using VisionStation.Storage.Entities;

namespace VisionStation.Storage;

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
        // 不能只调 EnsureCreated：它对已存在的库不做 ALTER TABLE，
        // 加一列后现场老库会在第一条保存时抛 "no such column"。
        var (journal, added) = await StorageSchema.EnsureUpToDateAsync(db).ConfigureAwait(false);

        if (added.Count > 0)
            _logger?.LogWarning("[DB] 结构升级：补充缺失列 {Columns}", string.Join(", ", added));
        _logger?.LogInformation("[DB] 数据库初始化完成（journal={Journal}）", journal);
        return true;
    });

    /// <summary>保存一次检测报告到数据库</summary>
    public Task SaveAsync(InspectionReport report) => RunAsync(async db =>
    {
        // 从 ToolResult 提取各工具结果（兼容老表结构）
        var p = report.PresenceResult;
        var d = report.DimensionResult;
        var f = report.DefectResult;

        var record = new InspectionRecord
        {
            Id = report.Id,
            Timestamp = report.Timestamp,
            TriggerSource = report.TriggerSource,
            ImagePath = report.ImagePath,

            // ── 有无检测 ──
            PresenceOk = p.IsOk,
            PresenceDetail = p.Summary,
            PresenceScore = p.Metrics.GetValueOrDefault("MatchScore", 0),

            // ── 尺寸检测 ──
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
