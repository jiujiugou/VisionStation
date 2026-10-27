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
///
/// <para>
/// <b>没有 Update、也没有单条 Delete</b>：审计的原则是"写下去就不再改"。
/// 唯一的删除入口是 <see cref="DeleteOlderThanAsync"/>（按时间整段过期），
/// 这样"这条审计被动过"在结构上就不可能发生 —— 比事后校验更可靠。
/// </para>
///
/// <para>
/// <b>写的人是后台线程，不是 UI</b>：<c>AuditService</c> 把调用方的
/// <c>Record(...)</c> 塞进有界队列，自己用一条独立的消费循环调
/// <see cref="SaveAsync"/>。所以本接口被调用的时机与按钮点击之间没有栈关系，
/// 写失败也不会走到用户面前（失败由消费循环记 Error 并继续处理下一条）。
/// </para>
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

    /// <summary>删除早于 cutoff 的记录，返回删除条数。
    ///
    /// <para>
    /// <b>当前没有生产调用方</b>：管家只清理检测记录（<c>HousekeepingService</c> 持有的是
    /// <see cref="IInspectionRepository"/>），审计的保留期还没接上。
    /// 也就是说审计表现在只增不减 —— 一年攒下来也就几万行，短期不是问题，
    /// 但"审计留一年"这条规则目前是靠"没人删"实现的，不是靠配置，别误以为它已经在跑。
    /// </para>
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoff);
}

/// <summary>
/// 审计数据访问实现。
///
/// 与 <see cref="InspectionRepository"/> 同样采用"每次操作一个 DbContext"：
/// 审计写入跑在后台消费线程上，检测保存跑在采集线程上，两者会同时碰这个库，
/// 共享一个 context 必然撞 EF 的并发禁止。
///
/// <para>
/// <b>归属</b>：<c>AddStorage</c> 注册为 DI 单例（活到容器释放），
/// 但**不持有 context**，只持有 <c>new</c> 它的工厂 —— 这是"注册成单例还能并发"的全部原因。
/// </para>
///
/// <para>
/// <b>失败行为</b>：抛。写失败由 <c>AuditService</c> 的消费循环捕获（节流记 Error，单条失败不停流）；
/// 关闭时 <c>Flush</c> 只等一个有界的时间片，超时就记 Warning 并取消 ——
/// 审计写不进去，不能让程序退不掉。
/// </para>
/// </summary>
public class AuditRepository : IAuditRepository
{
    /// <summary>取 context 的工厂；<c>Owned</c>=false 表示 context 由测试提供，不要替它释放。</summary>
    private readonly Func<(InspectionDbContext Db, bool Owned)> _newContext;

    /// <summary>日志。可空：测试不传也能用。</summary>
    private readonly ILogger<AuditRepository>? _logger;

    /// <summary>生产用：每次操作独立 context，可并发调用</summary>
    /// <param name="dbPath">已解析的绝对路径（<c>AppPaths.DbPath</c>），与检测记录同一个文件</param>
    public AuditRepository(string dbPath, ILogger<AuditRepository>? logger = null)
    {
        _newContext = () => (new InspectionDbContext(dbPath), true);
        _logger = logger;
    }

    /// <summary>测试/短生命周期用：复用传入的 context（调用方负责释放，且**不可并发**）</summary>
    public AuditRepository(InspectionDbContext db, ILogger<AuditRepository>? logger = null)
    {
        _newContext = () => (db, false);
        _logger = logger;
    }

    /// <summary>统一外壳：拿 context → 干活 → 只释放自己那份（异常原样上抛，不翻译）。</summary>
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

    /// <summary>建表 + 补结构（与检测仓储共用 <see cref="StorageSchema"/>，重复调用安全）。</summary>
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

    /// <summary>
    /// 追加一条审计。只有 <c>Add</c> + <c>SaveChanges</c>，没有任何"先查再改"。
    ///
    /// <para>
    /// 失败抛：由 <c>AuditService</c> 的消费循环捕获、计数、节流记 Error ——
    /// 单条失败不能停掉整条审计流，也不能把失败冒到按钮命令里（那会让一次"保存配方"
    /// 因为审计写不进去而报失败，实际上参数已经存下了）。
    /// </para>
    /// </summary>
    public Task SaveAsync(AuditEntry entry) => RunAsync(async db =>
    {
        db.AuditLogs.Add(ToRecord(entry));
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    });

    /// <summary>
    /// 最近 N 条，按**自增 Id** 倒序，不按 Timestamp。
    ///
    /// <para>
    /// 两条理由：① Id 是写入顺序，同一毫秒内写进来的两条也有确定先后，Timestamp 分不出来；
    /// ② 现场校时（NTP 回拨 / 手动改表）会让 Timestamp 出现"新的比旧的还早"，
    /// 那时按时间倒序会漏掉刚写的那几条 —— 而排障时最想看的正是"刚刚发生了什么"。
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<AuditEntry>> QueryRecentAsync(int count = 200) => RunAsync(async db =>
    {
        var rows = await db.AuditLogs
            .OrderByDescending(a => a.Id)
            .Take(count)
            .ToListAsync().ConfigureAwait(false);
        return (IReadOnlyList<AuditEntry>)rows.Select(ToEntry).ToList();
    });

    /// <summary>
    /// 按条件查询：动作 / 用户 / 起始时间，三者都可空（同时给就是 AND）。
    ///
    /// <para>
    /// <b>没有分页</b>：只有 <paramref name="count"/> 这个上限，超出的部分直接不返回。
    /// 界面据此提示"还有更多，请缩小时间范围"，而不是给一个翻页按钮 ——
    /// 审计页的用法是"定位某个时间段的某件事"，不是从头翻到尾。
    /// </para>
    /// <para>
    /// <paramref name="userName"/> 是**精确匹配**（不 Trim、不忽略大小写），
    /// 与 <see cref="UserRepository.FindAsync"/> 的宽松规则不同：
    /// 这里查的是"库里记的那个名字"，模糊匹配反而会把别人的记录带出来。
    /// 调用方（审计页）自己负责 Trim。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 按时间批量删（实现见接口上的说明：**当前无调用方**）。
    ///
    /// <para>
    /// 与 <see cref="InspectionRepository.DeleteOlderThanAsync"/> 的差别在这里只能是
    /// <c>Timestamp</c>：<see cref="AuditLogRecord"/> 没有单独的入库时间列 ——
    /// 审计的"发生时刻"和"记录时刻"本来就是一回事（后台队列最多晚几百毫秒）。
    /// <c>Timestamp</c> 上建了索引，这个 WHERE 不会全表扫。
    /// </para>
    /// <para>
    /// 先查后删是为了拿准确条数写日志；一个 <c>SaveChangesAsync</c> 就是一次事务，
    /// 删到一半失败会整轮回滚，不会留下删了一半的表。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 领域对象 → 实体。这里的 Trim 长度与实体上的 <c>[MaxLength]</c> 一一对应
    /// （64 / 24 / 120 / 500）—— 改列宽时必须同时改这里，否则 SQLite 不拦、EF 也不拦，
    /// 超长的字符串会安静地存进去，等到换数据库那天才炸。
    /// </summary>
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
