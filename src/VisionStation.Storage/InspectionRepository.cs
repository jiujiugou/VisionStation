using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VisionStation.Core.Models;
using VisionStation.Storage.Entities;

namespace VisionStation.Storage;

/// <summary>
/// 检测记录数据访问层 —— SQLite 的完整 CRUD
///
/// 职责：
///   1. 保存每次检测的结果（InspectionReport → InspectionRecord）
///   2. 查询最近 N 条记录
///   3. 统计 OK/NG 数量
///   4. 删除过期记录（HousekeepingService 调用）
///   5. 确保数据库和表已创建
///
/// 为什么单独一层？
///   如果直接让 MainViewModel 操作 DbContext，ViewModel 就需要知道 EF Core、
///   SQLite 路径、Connections tring——违反了分层原则。
///   Repository 把这些 SQL 细节封装，ViewModel 只调 SaveAsync / GetStatisticsAsync。
/// </summary>
public class InspectionRepository
{
    private readonly InspectionDbContext _db;
    private readonly ILogger<InspectionRepository>? _logger;

    public InspectionRepository(InspectionDbContext db, ILogger<InspectionRepository>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>确保数据库和表已创建（SQLite 文件不存在 → 自动创建）</summary>
    public async Task EnsureCreatedAsync()
    {
        await _db.Database.EnsureCreatedAsync();
        _logger?.LogInformation("[DB] 数据库初始化完成");
    }

    /// <summary>保存一次检测报告到数据库</summary>
    public async Task SaveAsync(InspectionReport report)
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

        _db.InspectionRecords.Add(record);
        await _db.SaveChangesAsync();
        _logger?.LogInformation("[DB] 保存记录 ID:{Id} 判定:{Verdict}", report.Id, report.OverallVerdict);
    }

    /// <summary>查询最近 N 条记录（按时间倒序）</summary>
    public async Task<List<InspectionRecord>> GetRecentAsync(int count = 100)
    {
        return await _db.InspectionRecords
            .OrderByDescending(r => r.CreatedAt)
            .Take(count)
            .ToListAsync();
    }

    /// <summary>统计 OK/NG 数量（可选时间范围）</summary>
    public async Task<(int OkCount, int NgCount)> GetStatisticsAsync(DateTime? since = null)
    {
        var query = _db.InspectionRecords.AsQueryable();
        if (since.HasValue)
            query = query.Where(r => r.Timestamp >= since.Value);

        var groups = await query
            .GroupBy(r => r.OverallVerdict)
            .Select(g => new { Verdict = g.Key, Count = g.Count() })
            .ToListAsync();

        int okCount = groups.FirstOrDefault(g => g.Verdict == "OK")?.Count ?? 0;
        int ngCount = groups.FirstOrDefault(g => g.Verdict == "NG")?.Count ?? 0;
        return (okCount, ngCount);
    }

    /// <summary>批量删除过期记录，返回删除条数（管家服务调用）</summary>
    /// <param name="cutoff">此时间之前的记录全部删除</param>
    public async Task<int> DeleteOlderThanAsync(DateTime cutoff)
    {
        var old = await _db.InspectionRecords
            .Where(r => r.CreatedAt < cutoff)
            .ToListAsync();

        if (old.Count == 0) return 0;

        _db.InspectionRecords.RemoveRange(old);
        await _db.SaveChangesAsync();
        _logger?.LogInformation("[DB] 删除 {Count} 条过期记录 (早于 {Cutoff:yyyy-MM-dd})",
            old.Count, cutoff);
        return old.Count;
    }
}
