using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VisionStation.Core.Users;
using VisionStation.Storage.Entities;

namespace VisionStation.Storage;

/// <summary>
/// 用户账号数据访问契约。
///
/// 与检测记录/审计分开：账号表是全系统唯一会被 UPDATE 的表
/// （改密码、停用、记最后登录），而另外两张是纯追加。混在一起，
/// 将来想给"只追加"的表做防篡改时会很别扭。
/// </summary>
public interface IUserRepository
{
    Task EnsureCreatedAsync();

    /// <summary>按登录名查（大小写不敏感）。找不到返回 null。</summary>
    Task<UserAccount?> FindAsync(string userName);

    /// <summary>全部账号（含已停用 —— 用户管理界面要能看到"这个人被停用了"）</summary>
    Task<IReadOnlyList<UserAccount>> GetAllAsync();

    /// <summary>新增或按 UserName 覆盖</summary>
    Task UpsertAsync(UserAccount user);

    /// <summary>只改密码材料（登录成功后自动升级轮数也走这里）</summary>
    Task UpdatePasswordAsync(string userName, string hash, string salt, int iterations, bool mustChangePassword);

    /// <summary>登录成功时记一笔时间</summary>
    Task TouchLoginAsync(string userName, DateTime at);

    Task SetEnabledAsync(string userName, bool enabled);
}

/// <summary>
/// 用户账号数据访问实现。
///
/// 与另外两个仓储相同的惯例：每次操作一个 DbContext（登录发生在 UI 线程，
/// 用户管理可能并发，共享 context 会撞 EF 的并发禁止）。
/// </summary>
public class UserRepository : IUserRepository
{
    private readonly Func<(InspectionDbContext Db, bool Owned)> _newContext;
    private readonly ILogger<UserRepository>? _logger;

    public UserRepository(string dbPath, ILogger<UserRepository>? logger = null)
    {
        _newContext = () => (new InspectionDbContext(dbPath), true);
        _logger = logger;
    }

    public UserRepository(InspectionDbContext db, ILogger<UserRepository>? logger = null)
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
            _logger?.LogWarning("[用户] 结构升级：新建缺失表 {Tables}", string.Join(", ", addedTables));
        if (addedColumns.Count > 0)
            _logger?.LogWarning("[用户] 结构升级：补充缺失列 {Columns}", string.Join(", ", addedColumns));
        _logger?.LogInformation("[用户] 表就绪（journal={Journal}）", journal);
        return true;
    });

    public Task<UserAccount?> FindAsync(string userName) => RunAsync(async db =>
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;

        // 大小写不敏感：操作员在工控机上用小键盘输名字，没人会注意大小写。
        // 表只有个位数行，lower() 用不上索引也无所谓。
        var key = userName.Trim().ToLowerInvariant();
        var row = await db.Users
            .FirstOrDefaultAsync(u => u.UserName.ToLower() == key)
            .ConfigureAwait(false);

        return row is null ? null : ToAccount(row);
    });

    public Task<IReadOnlyList<UserAccount>> GetAllAsync() => RunAsync(async db =>
    {
        var rows = await db.Users.OrderBy(u => u.UserName).ToListAsync().ConfigureAwait(false);
        return (IReadOnlyList<UserAccount>)rows.Select(ToAccount).ToList();
    });

    public Task UpsertAsync(UserAccount user) => RunAsync(async db =>
    {
        var existing = await db.Users
            .FirstOrDefaultAsync(u => u.UserName == user.UserName)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.Users.Add(ToRecord(user));
        }
        else
        {
            existing.DisplayName = user.DisplayName;
            existing.Role = user.Role;
            existing.PasswordHash = user.PasswordHash;
            existing.PasswordSalt = user.PasswordSalt;
            existing.PasswordIterations = user.PasswordIterations;
            existing.IsEnabled = user.IsEnabled;
            existing.MustChangePassword = user.MustChangePassword;
            existing.LastLoginAt = user.LastLoginAt;
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        _logger?.LogInformation("[用户] 保存账号 {User}（角色：{Role}）",
            user.UserName, RolePermissions.Describe(user.Role));
        return true;
    });

    public Task UpdatePasswordAsync(
        string userName, string hash, string salt, int iterations, bool mustChangePassword) => RunAsync(async db =>
    {
        var row = await db.Users.FirstOrDefaultAsync(u => u.UserName == userName).ConfigureAwait(false);
        if (row is null)
            throw new InvalidOperationException($"账号不存在：{userName}");

        row.PasswordHash = hash;
        row.PasswordSalt = salt;
        row.PasswordIterations = iterations;
        row.MustChangePassword = mustChangePassword;

        await db.SaveChangesAsync().ConfigureAwait(false);
        _logger?.LogInformation("[用户] 已更新密码材料 {User}（轮数 {Iterations}）", userName, iterations);
        return true;
    });

    public Task TouchLoginAsync(string userName, DateTime at) => RunAsync(async db =>
    {
        var row = await db.Users.FirstOrDefaultAsync(u => u.UserName == userName).ConfigureAwait(false);
        if (row is null) return false;

        row.LastLoginAt = at;
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    });

    public Task SetEnabledAsync(string userName, bool enabled) => RunAsync(async db =>
    {
        var row = await db.Users.FirstOrDefaultAsync(u => u.UserName == userName).ConfigureAwait(false);
        if (row is null) return false;

        row.IsEnabled = enabled;
        await db.SaveChangesAsync().ConfigureAwait(false);
        _logger?.LogWarning("[用户] {Action}账号 {User}", enabled ? "启用" : "停用", userName);
        return true;
    });

    private static UserAccount ToAccount(UserRecord r) => new()
    {
        UserName = r.UserName,
        DisplayName = r.DisplayName,
        Role = r.Role,
        PasswordHash = r.PasswordHash,
        PasswordSalt = r.PasswordSalt,
        PasswordIterations = r.PasswordIterations,
        IsEnabled = r.IsEnabled,
        MustChangePassword = r.MustChangePassword,
        CreatedAt = r.CreatedAt,
        LastLoginAt = r.LastLoginAt,
    };

    private static UserRecord ToRecord(UserAccount a) => new()
    {
        UserName = a.UserName,
        DisplayName = a.DisplayName,
        Role = a.Role,
        PasswordHash = a.PasswordHash,
        PasswordSalt = a.PasswordSalt,
        PasswordIterations = a.PasswordIterations,
        IsEnabled = a.IsEnabled,
        MustChangePassword = a.MustChangePassword,
        CreatedAt = a.CreatedAt,
        LastLoginAt = a.LastLoginAt,
    };
}
