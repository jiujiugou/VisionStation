using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VisionStation.Core.Models;
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
/// 修复（F-111）：原来 DI 注册的是「全进程一个 InspectionDbContext」，
/// 而管家清理（Timer 线程）与检测保存（采集线程）会同时用它，
/// EF Core 明确禁止同一 context 并发操作 —— 结果是 SaveChanges 抛异常、检测记录直接丢。
///
/// 现在：持有 dbPath，每次操作新建并释放一个 DbContext（EF Core 的标准做法）。
/// 保留接受现成 DbContext 的构造，仅供单元测试与短生命周期场景使用。
/// </summary>
public class InspectionRepository : IInspectionRepository
{
    private readonly Func<(InspectionDbContext Db, bool Owned)> _newContext;
    private readonly ILogger<InspectionRepository>? _logger;

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
        finally
        {
            if (owned) await db.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>确保数据库和表已创建（SQLite 文件不存在 → 自动创建）</summary>
    public Task EnsureCreatedAsync() => RunAsync(async db =>
    {
        await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
        _logger?.LogInformation("[DB] 数据库初始化完成");
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
