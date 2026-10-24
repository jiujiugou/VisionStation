using Microsoft.Extensions.Logging;
using VisionStation.Core.Models;
using VisionStation.Core.Users;
using VisionStation.Service.Audit;
using VisionStation.Storage;

namespace VisionStation.Service.Security;

/// <summary>认证结果分类。分得细是为了给操作员不同的提示，也为了审计能区分"谁在试"。</summary>
public enum AuthOutcome
{
    Success,
    UserNotFound,
    WrongPassword,
    Disabled,
    LockedOut,
}

/// <summary>
/// 一次认证的结果。
/// </summary>
/// <param name="Outcome">结果分类</param>
/// <param name="User">成功时的账号；失败为 null</param>
/// <param name="LockRemaining">被锁时的剩余时间</param>
public sealed record AuthResult(AuthOutcome Outcome, UserAccount? User, TimeSpan? LockRemaining = null)
{
    public bool Succeeded => Outcome == AuthOutcome.Success;

    /// <summary>
    /// 给操作员看的提示。
    ///
    /// "账号不存在" 与 "密码错误" 分开说 —— 工控机不是公网系统，
    /// 账号名本来就贴在交接班表上，藏起来只会让操作员反复试。
    /// 真正的防线是失败锁定，不是含糊其辞。
    /// </summary>
    public string Message => Outcome switch
    {
        AuthOutcome.Success => "登录成功",
        AuthOutcome.UserNotFound => "账号不存在",
        AuthOutcome.WrongPassword => "密码错误",
        AuthOutcome.Disabled => "该账号已停用，请联系管理员",
        AuthOutcome.LockedOut => LockRemaining is { } r
            ? $"连续登录失败次数过多，请 {(int)Math.Ceiling(r.TotalSeconds)} 秒后再试"
            : "连续登录失败次数过多，请稍后再试",
        _ => "登录失败",
    };
}

/// <summary>
/// 认证与账号管理契约。
///
/// 注意这里没有"当前登录的是谁" —— 那是 <see cref="ISessionService"/> 的职责。
/// 分开的理由：认证是一次性动作（对密码），会话是持续状态（会超时、会降级）。
/// 合成一个类，测试时就要为了验"降级"而先构造一次真实认证。
/// </summary>
public interface IUserService
{
    Task<AuthResult> AuthenticateAsync(string userName, string password, CancellationToken ct = default);

    /// <summary>改密。返回 (成功, 失败原因)。</summary>
    Task<(bool Ok, string? Error)> ChangePasswordAsync(
        string userName, string currentPassword, string newPassword, CancellationToken ct = default);

    /// <summary>库为空时创建默认管理员（出厂密码 + 强制改密）</summary>
    Task EnsureDefaultAdminAsync(CancellationToken ct = default);

    Task<IReadOnlyList<UserAccount>> GetUsersAsync(CancellationToken ct = default);
}

/// <summary>
/// 认证服务。
///
/// 三条设计决定：
///
/// 1) <b>失败计数放内存不放库</b>。账户锁定是用来挡在线爆破的，而工控机重启是
///    有人到现场的物理动作 —— 重启清零不构成风险。写库反而要在每次密码错时
///    多做一次写，而那时数据库可能正被管家清理锁着。
///
/// 2) <b>轮数自动升级</b>。登录成功后如果库里的迭代轮数低于当前标准，
///    就拿这次输入的明文重算一遍存回去；用户下次登录就完成升级，无感。
///
/// 3) <b>审计在这里记，不交给调用方</b>。认证失败的原因（账号不存在 / 密码错 /
///    被停用）只有服务内部知道，交给界面记必然记不全。
/// </summary>
public sealed class UserService : IUserService
{
    /// <summary>连续失败多少次触发锁定</summary>
    public const int MaxFailuresBeforeLock = 3;

    /// <summary>锁定时长</summary>
    public static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(60);

    /// <summary>出厂默认管理员</summary>
    public const string DefaultAdminUserName = "admin";

    /// <summary>
    /// 出厂默认密码。
    ///
    /// 明文写在源码里是刻意的：工控设备的出厂密码本来就印在说明书上，
    /// 藏起来只会变成"装机的人自己也不知道密码"。
    /// 真正的保护是 <see cref="UserAccount.MustChangePassword"/> —— 首次登录必须改，
    /// 改之前每一次登录都写进审计。
    /// </summary>
    public const string DefaultAdminPassword = "1234";

    /// <summary>新密码最短长度。工控现场是戴手套敲数字键，不追求复杂度，靠锁定 + 审计兜底。</summary>
    public const int MinPasswordLength = 4;

    private sealed class FailureState
    {
        public int Count;
        public DateTime? LockedUntil;
    }

    private readonly IUserRepository _repo;
    private readonly IAuditService _audit;
    private readonly ILogger<UserService>? _logger;
    private readonly Func<DateTime> _now;

    /// <summary>用户名 → 失败状态。键用不区分大小写的比较器，与查库规则一致。</summary>
    private readonly Dictionary<string, FailureState> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public UserService(
        IUserRepository repo,
        IAuditService audit,
        ILogger<UserService>? logger = null,
        Func<DateTime>? now = null)
    {
        _repo = repo;
        _audit = audit;
        _logger = logger;
        _now = now ?? (() => DateTime.Now);
    }

    public async Task EnsureDefaultAdminAsync(CancellationToken ct = default)
    {
        var all = await _repo.GetAllAsync().ConfigureAwait(false);
        if (all.Count > 0) return;

        var pwd = PasswordHasher.Create(DefaultAdminPassword);

        await _repo.UpsertAsync(new UserAccount
        {
            UserName = DefaultAdminUserName,
            DisplayName = "管理员",
            Role = UserRole.Admin,
            PasswordHash = pwd.Hash,
            PasswordSalt = pwd.Salt,
            PasswordIterations = pwd.Iterations,
            IsEnabled = true,
            MustChangePassword = true,
            CreatedAt = _now(),
        }).ConfigureAwait(false);

        _audit.RecordAs("system", "系统", AuditAction.UserCreate, DefaultAdminUserName,
            detail: "首次启动创建出厂管理员，密码为出厂值，首次登录必须修改");

        _logger?.LogWarning(
            "[用户] 已创建出厂管理员 {User}（密码 {Password}），首次登录必须改密",
            DefaultAdminUserName, DefaultAdminPassword);
    }

    public async Task<AuthResult> AuthenticateAsync(
        string userName, string password, CancellationToken ct = default)
    {
        userName = (userName ?? string.Empty).Trim();

        // 空输入不查库、不计数：这不是"试密码"，是没输
        if (userName.Length == 0 || string.IsNullOrEmpty(password))
            return new AuthResult(AuthOutcome.WrongPassword, null);

        // 先看锁 —— 锁定期内连库都不查，爆破也就打不进来
        if (TryGetLock(userName, out var remaining))
        {
            _audit.RecordAs(userName, "未登录", AuditAction.LoginLocked, userName,
                result: AuditResult.Denied,
                detail: $"锁定期内再次尝试登录，剩余 {(int)remaining.TotalSeconds} 秒");
            return new AuthResult(AuthOutcome.LockedOut, null, remaining);
        }

        UserAccount? user;
        try
        {
            user = await _repo.FindAsync(userName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[用户] 查询账号失败，本次登录按失败处理");
            _audit.RecordAs(userName, "未登录", AuditAction.LoginFailed, userName,
                result: AuditResult.Failed, detail: $"账号查询异常：{ex.Message}");
            return new AuthResult(AuthOutcome.WrongPassword, null);
        }

        if (user is null)
        {
            var locked = RegisterFailure(userName);
            _audit.RecordAs(userName, "未登录", AuditAction.LoginFailed, userName,
                result: AuditResult.Denied, detail: "账号不存在");
            if (locked) RecordLock(userName);
            return new AuthResult(AuthOutcome.UserNotFound, null);
        }

        if (!user.IsEnabled)
        {
            _audit.RecordAs(user.UserName, user.RoleText, AuditAction.LoginFailed, user.UserName,
                result: AuditResult.Denied, detail: "账号已停用");
            return new AuthResult(AuthOutcome.Disabled, null);
        }

        if (!PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
        {
            var locked = RegisterFailure(user.UserName);
            _audit.RecordAs(user.UserName, user.RoleText, AuditAction.LoginFailed, user.UserName,
                result: AuditResult.Denied, detail: "密码错误");
            if (locked) RecordLock(user.UserName);
            return new AuthResult(AuthOutcome.WrongPassword, null);
        }

        ClearFailures(user.UserName);

        // 轮数升级：这次拿到了明文，正好把老哈希换成当前标准
        if (PasswordHasher.NeedsUpgrade(user.PasswordIterations))
        {
            try
            {
                var fresh = PasswordHasher.Create(password);
                await _repo.UpdatePasswordAsync(
                    user.UserName, fresh.Hash, fresh.Salt, fresh.Iterations, user.MustChangePassword)
                    .ConfigureAwait(false);

                user = user with
                {
                    PasswordHash = fresh.Hash,
                    PasswordSalt = fresh.Salt,
                    PasswordIterations = fresh.Iterations,
                };

                _logger?.LogInformation("[用户] {User} 的密码轮数已升级到 {Iterations}",
                    user.UserName, fresh.Iterations);
            }
            catch (Exception ex)
            {
                // 升级失败不影响本次登录 —— 下次登录再升
                _logger?.LogWarning(ex, "[用户] {User} 密码轮数升级失败，本次仍按原轮数通过", user.UserName);
            }
        }

        try
        {
            await _repo.TouchLoginAsync(user.UserName, _now()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[用户] 记录最后登录时间失败，不影响登录");
        }

        _audit.RecordAs(user.UserName, user.RoleText, AuditAction.Login, user.UserName,
            detail: user.MustChangePassword ? "登录成功（使用的是出厂密码，需立即修改）" : "登录成功");

        return new AuthResult(AuthOutcome.Success, user);
    }

    public async Task<(bool Ok, string? Error)> ChangePasswordAsync(
        string userName, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await _repo.FindAsync(userName).ConfigureAwait(false);
        if (user is null) return (false, "账号不存在");

        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
            return (false, "当前密码不正确");

        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < MinPasswordLength)
            return (false, $"新密码至少 {MinPasswordLength} 位");

        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
            return (false, "新密码不能与当前密码相同");

        var fresh = PasswordHasher.Create(newPassword);
        await _repo.UpdatePasswordAsync(
            user.UserName, fresh.Hash, fresh.Salt, fresh.Iterations, mustChangePassword: false)
            .ConfigureAwait(false);

        _audit.RecordAs(user.UserName, user.RoleText, AuditAction.PasswordChange, user.UserName,
            detail: "用户自行修改密码");

        return (true, null);
    }

    public Task<IReadOnlyList<UserAccount>> GetUsersAsync(CancellationToken ct = default)
        => _repo.GetAllAsync();

    // ── 失败计数（内存，见类注释第 1 条） ──

    private bool TryGetLock(string userName, out TimeSpan remaining)
    {
        lock (_gate)
        {
            remaining = TimeSpan.Zero;
            if (!_failures.TryGetValue(userName, out var state) || state.LockedUntil is not { } until)
                return false;

            var left = until - _now();
            if (left <= TimeSpan.Zero)
            {
                // 锁过期：清零重新计数，否则下一次失败会立刻再锁
                state.Count = 0;
                state.LockedUntil = null;
                return false;
            }

            remaining = left;
            return true;
        }
    }

    /// <summary>记一次失败。返回 true 表示本次刚好触发锁定。</summary>
    private bool RegisterFailure(string userName)
    {
        lock (_gate)
        {
            if (!_failures.TryGetValue(userName, out var state))
            {
                state = new FailureState();
                _failures[userName] = state;
            }

            state.Count++;

            if (state.Count < MaxFailuresBeforeLock) return false;

            state.LockedUntil = _now() + LockDuration;
            return true;
        }
    }

    private void ClearFailures(string userName)
    {
        lock (_gate)
        {
            _failures.Remove(userName);
        }
    }

    private void RecordLock(string userName)
    {
        _audit.RecordAs(userName, "未登录", AuditAction.LoginLocked, userName,
            result: AuditResult.Denied,
            detail: $"连续失败 {MaxFailuresBeforeLock} 次，锁定 {(int)LockDuration.TotalSeconds} 秒");
        _logger?.LogWarning("[用户] {User} 连续失败 {Count} 次，已锁定 {Seconds} 秒",
            userName, MaxFailuresBeforeLock, (int)LockDuration.TotalSeconds);
    }
}
