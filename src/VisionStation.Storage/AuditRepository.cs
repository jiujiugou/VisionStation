using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VisionStation.Core.Models;
using VisionStation.Storage.Entities;

namespace VisionStation.Storage;

/// <summary>
/// 审计数据访问契约。
///
/// 与 <see cref="IInspectionRepository"/> 分开而不是合并：
/// 检测记录和审计记录的生命周期完全不同 —— 检测记录 30 天后由管家删掉，
/// 审计要按年留（出了质量事故去翻三个月前的记录是常态）。混在一个接口里，
/// 将来给审计加"保留一年"的清理规则时，会连带把检测记录的清理语义搅乱。
/// </summary>
public interface IAuditRepository
{
    /// <summary>确保表已存在（走与检测记录同一套 schema 升级器）</summary>
    Task EnsureCreatedAsync();

    /// <summary>追加一条审计。纯 insert，不查不改 —— 审计一旦写入就不该被修改</summary>
    Task SaveAsync(AuditEntry entry);

    /// <summary>最近 N 条（按时间倒序），供界面与排障使用</summary>
    Task<IReadOnlyList<AuditEntry>> QueryRecentAsync(int count = 200);

    /// <summary>按条件查询：动作 / 用户 / 起始时间，三者都可空</summary>
    Task<IReadOnlyList<AuditEntry>> QueryAsync(
        AuditAction? action = null,
        string? userName = null,
        DateTime? since = null,
        int count = 200);

    /// <summary>删除早于 cutoff 的记录，返回删除条数</summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoff);
}

/// <summary>
/// 审计数据访问实现。
///
/// 与 <see cref="InspectionRepository"/> 同样采用"每次操作一个 DbContext"：
/// 审计写入跑在后台消费线程上，检测保存跑在采集线程上，两者会同时碰这个库，
/// 共享一个 context 必然撞 EF 的并发禁止。
/// </summary>
public class AuditRepository : IAuditRepository
{
    private readonly Func<(InspectionDbContext Db, bool Owned)> _newContext;
    private readonly ILogger<AuditRepository>? _logger;

    /// <summary>生产用：每次操作独立 context，可并发调用</summary>
    public AuditRepository(string dbPath, ILogger<AuditRepository>? logger = null)
    {
        _newContext = () => (new InspectionDbContext(dbPath), true);
        _logger = logger;
    }

    /// <summary>测试/短生命周期用：复用传入的 context（调用方负责释放）</summary>
    public AuditRepository(InspectionDbContext db, ILogger<AuditRepository>? logger = null)
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

    public Task EnsureCreatedAsync() => RunAsync(async db =>
    {
        var (journal, addedTables, addedColumns) =
            await StorageSchema.EnsureUpToDateAsync(db).ConfigureAwait(false);

        if (addedTables.Count > 0)
            _logger?.LogWarning("[审计] 结构升级：新建缺失表 {Tables}", string.Join(", ", addedTables));
        if (addedColumns.Count > 0)
            _logger?.LogWarning("[审计] 结构升级：补充缺失列 {Columns}", string.Join(", ", addedColumns));
        _logger?.LogInformation("[审计] 表就绪（journal={Journal}）", journal);
        return true;
    });

    public Task SaveAsync(AuditEntry entry) => RunAsync(async db =>
    {
        db.AuditLogs.Add(ToRecord(entry));
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    });

    public Task<IReadOnlyList<AuditEntry>> QueryRecentAsync(int count = 200) => RunAsync(async db =>
    {
        var rows = await db.AuditLogs
            .OrderByDescending(a => a.Id)
            .Take(count)
            .ToListAsync().ConfigureAwait(false);
        return (IReadOnlyList<AuditEntry>)rows.Select(ToEntry).ToList();
    });

    public Task<IReadOnlyList<AuditEntry>> QueryAsync(
        AuditAction? action = null,
        string? userName = null,
        DateTime? since = null,
        int count = 200) => RunAsync(async db =>
    {
        var query = db.AuditLogs.AsQueryable();

        if (action.HasValue)
            query = query.Where(a => a.Action == action.Value);
        if (!string.IsNullOrWhiteSpace(userName))
            query = query.Where(a => a.UserName == userName);
        if (since.HasValue)
            query = query.Where(a => a.Timestamp >= since.Value);

        var rows = await query
            .OrderByDescending(a => a.Id)
            .Take(count)
            .ToListAsync().ConfigureAwait(false);
        return (IReadOnlyList<AuditEntry>)rows.Select(ToEntry).ToList();
    });

    public Task<int> DeleteOlderThanAsync(DateTime cutoff) => RunAsync(async db =>
    {
        var old = await db.AuditLogs
            .Where(a => a.Timestamp < cutoff)
            .ToListAsync().ConfigureAwait(false);

        if (old.Count == 0) return 0;

        db.AuditLogs.RemoveRange(old);
        await db.SaveChangesAsync().ConfigureAwait(false);
        _logger?.LogInformation("[审计] 删除 {Count} 条过期记录（早于 {Cutoff:yyyy-MM-dd}）",
            old.Count, cutoff);
        return old.Count;
    });

    private static AuditLogRecord ToRecord(AuditEntry e) => new()
    {
        Timestamp = e.Timestamp,
        UserName = Trim(e.UserName, 64),
        UserRole = Trim(e.UserRole, 24),
        Action = e.Action,
        Target = Trim(e.Target, 120),
        Before = TrimNullable(e.Before, 500),
        After = TrimNullable(e.After, 500),
        Result = e.Result,
        Detail = TrimNullable(e.Detail, 500),
    };

    private static AuditEntry ToEntry(AuditLogRecord r) => new()
    {
        Id = r.Id,
        Timestamp = r.Timestamp,
        UserName = r.UserName,
        UserRole = r.UserRole,
        Action = r.Action,
        Target = r.Target,
        Before = r.Before,
        After = r.After,
        Result = r.Result,
        Detail = r.Detail,
    };

    // 超长直接截断而不是抛异常：审计写入失败会连着把一整条记录弄丢，
    // 截断至少保住了"谁、何时、做了什么"。
    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max];
    }

    private static string? TrimNullable(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= max ? value : value[..max];
    }
}
