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
/// 一次管理动作（新增 / 停用启用 / 重置密码 / 改角色）的结果。
///
/// <para>
/// 为什么不用异常表达失败：这些失败绝大多数是<b>业务上的"不允许"</b>
/// （撞名、最后一个管理员、密码太短），界面要原样展示给人看。
/// 抛异常会让"业务拒绝"和"数据库坏了"混在同一层 catch 里，
/// 最后两边都只剩一句"操作失败"。
/// </para>
/// <para>
/// <b>没有</b> <c>Denied</c> 字段：权限判定在界面层（<c>ISessionService.HasPermission</c>），
/// 到不了这里 —— 本类不持有会话，也无从知道"现在登录的是谁"（<c>actor</c> 是调用方传进来的，
/// 只用于自保护判定，不是权限判定）。
/// </para>
/// </summary>
/// <param name="Ok">是否真的改了库</param>
/// <param name="Error">失败原因，直接给操作员看</param>
public sealed record UserAdminResult(bool Ok, string? Error = null)
{
    /// <summary>成功（同一个实例可复用，它是不可变的）</summary>
    public static readonly UserAdminResult Success = new(true);

    /// <summary>失败：<paramref name="error"/> 必须是能直接显示的中文句子</summary>
    public static UserAdminResult Fail(string error) => new(false, error);
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

    // ── 用户管理（F-64） ──
    //
    // 四个方法的 actor 参数是"当前操作人的登录名"，只用于两条自保护判定
    // （不能停用自己 / 不能改掉最后一个启用中的管理员）。
    // 权限判定不在这里：ISessionService.HasPermission(ManageUsers) 由界面层先做一遍，
    // 本类不持有会话，也无从伪造一个"当前用户"。

    /// <summary>
    /// 新建账号。新号一律 <c>MustChangePassword = true</c>（与出厂管理员同一口径），
    /// 用户名大小写不敏感唯一。
    /// </summary>
    Task<UserAdminResult> CreateUserAsync(
        string userName, UserRole role, string initialPassword, string actor,
        CancellationToken ct = default);

    /// <summary>
    /// 启用 / 停用。停用之后 <see cref="AuthenticateAsync"/> 返回 <see cref="AuthOutcome.Disabled"/>，
    /// 但账号记录与全部历史审计都留着。
    /// </summary>
    Task<UserAdminResult> SetUserEnabledAsync(
        string userName, bool enabled, string actor, CancellationToken ct = default);

    /// <summary>
    /// 管理员重置他人密码（不需要旧密码），重置后强制本人下次登录改密。
    /// 这是 <see cref="ChangePasswordAsync"/> 缺失的那条路径，不是它的替代 —— 两者都要留着。
    /// </summary>
    Task<UserAdminResult> ResetPasswordAsync(
        string userName, string newPassword, string actor, CancellationToken ct = default);

    /// <summary>改角色。降掉最后一个启用中的管理员会让系统锁死，因此被拒。</summary>
    Task<UserAdminResult> ChangeRoleAsync(
        string userName, UserRole role, string actor, CancellationToken ct = default);
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
///
/// <para>
/// <b>定位</b>：登录链路上的第一环（认证 → 建会话 → 界面按权限开合）。
/// 上层调用方是登录窗 VM（<c>LoginViewModel</c>）与启动流程
/// （<c>AppHost.InitializeAsync</c> 调 <see cref="EnsureDefaultAdminAsync"/>）。
/// 本类**只管密码与账号状态**：不建会话、不做权限判断 —— 那是
/// <see cref="ISessionService"/> 的事，分开才能单独测"降级"而不用先造一次真实认证。
/// </para>
///
/// <para>
/// <b>归属</b>：DI 单例，活到进程结束（失败计数必须跨请求存活，否则锁定无意义）。
/// 无 UI 依赖，异步方法内部 <c>ConfigureAwait(false)</c> 续体会落在任意线程池线程；
/// 唯一被多线程共享的可变状态是失败计数字典，由 <c>_gate</c> 保护。
/// </para>
///
/// <para>
/// <b>失败行为</b>：<b>从不向调用方抛业务异常</b>，全部翻译成返回值 ——
/// 查询账号时数据库异常 → 记日志 + 审计 + 当作登录失败返回；
/// 轮数升级失败 → 记警告，本次照常登录；记录最后登录时间失败 → 记警告，不影响登录。
/// 理由都一样：登录失败要能给出人话提示，抛异常只会变成"程序崩了"。
/// </para>
///
/// <para>
/// <b>最容易被问到的判断</b>：账号不存在也照样计入失败次数（<c>RegisterFailure</c>），
/// 否则"枚举账号名"就能免费无限次试探；代价是打错用户名也会锁 60 秒 —— 这是刻意接受的。
/// </para>
/// </summary>
public sealed class UserService : IUserService
{
    /// <summary>连续失败多少次触发锁定</summary>
    public const int MaxFailuresBeforeLock = 3;

    /// <summary>锁定时长。60 秒足够让撞库变成不划算，又不至于让夜班操作员等太久。</summary>
    public static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(60);

    /// <summary>出厂默认管理员的登录名。改了它，现场说明书和交接班表都要跟着改。</summary>
    public const string DefaultAdminUserName = "admin";

    /// <summary>
    /// 出厂默认口令 —— 值由常量自己携带，<b>注释里不复述</b>
    /// （给别人看的代码里写明文等于泄露；要看值只能看这一行常量）。
    ///
    /// <para>
    /// 明文常量存在源码里是有意为之：工控设备的出厂口令本来就要写进出厂说明，
    /// 藏成"每次装机自己猜"只会让人把口令贴在机箱上。
    /// 真正的保护不是这个字符串，而是<b>首次登录必须改密</b>：
    /// 账号由 <see cref="EnsureDefaultAdminAsync"/> 建成 <c>MustChangePassword = true</c>，
    /// 登录窗（<c>LoginViewModel</c>）看到这一位就强制走改密流程、
    /// 不让直接进主界面；改完再用新口令重新认证一次拿到更新后的账号。
    /// </para>
    /// <para>
    /// 取证：只要有人还在这份口令上登录，每次成功的审计 detail 都会带
    /// "使用的是出厂密码，需立即修改"（见 <see cref="AuthenticateAsync"/> 末尾），
    /// 复盘时一眼能看出"这台机器从装机起就没人改过密码"。
    /// </para>
    /// <para>
    /// <b>已知副作用</b>：<see cref="EnsureDefaultAdminAsync"/> 建号时会把该口令打进日志
    /// （方便首次装机的人照抄），于是 <c>Logs</c> 目录里就留了一份明文。
    /// 交付现场的机器在首次改密后应当清一次日志 —— 这一点代码没有替你兜。
    /// </para>
    /// </summary>
    public const string DefaultAdminPassword = "1234";

    /// <summary>新密码最短长度。工控现场是戴手套敲数字键，不追求复杂度，靠锁定 + 审计兜底。</summary>
    public const int MinPasswordLength = 4;

    /// <summary>
    /// 单个账号的失败状态。<c>LockedUntil</c> 为 null = 还没锁；
    /// 计数与锁定时间放在同一个对象里，避免"计数清零了但锁还在"这种半状态。
    /// </summary>
    private sealed class FailureState
    {
        public int Count;
        public DateTime? LockedUntil;
    }

    /// <summary>账号仓储。唯一的数据来源，本类不建表、不写 SQL。</summary>
    private readonly IUserRepository _repo;

    /// <summary>
    /// 审计。认证的每一次尝试（成功/失败/被锁）都要留痕 ——
    /// 这是"谁在试密码"唯一的线索，所以放在服务里而不是交给界面。
    /// </summary>
    private readonly IAuditService _audit;

    /// <summary>日志。可空：单测里不关心日志时不必造一个 ILogger。</summary>
    private readonly ILogger<UserService>? _logger;

    /// <summary>时间源。注入是为了让"锁 60 秒后自动解锁"能被单测瞬间验证。</summary>
    private readonly Func<DateTime> _now;

    /// <summary>用户名 → 失败状态。键用不区分大小写的比较器，与查库规则一致。</summary>
    private readonly Dictionary<string, FailureState> _failures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>失败字典的闸门。后台线程可能同时来两次登录，不加锁会丢计数（= 少锁一次）。</summary>
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

    /// <summary>
    /// 库里的账号表为空时建一个出厂管理员（登录名见 <see cref="DefaultAdminUserName"/>，
    /// 口令由 <see cref="DefaultAdminPassword"/> 提供，强制首次改密）。
    ///
    /// <para>
    /// 判据是"表里一个人都没有"，不是"admin 不存在"：现场自己建过账号之后，
    /// 这个方法就永久不再起作用 —— 否则管理员被删了会被它悄悄重建一个出厂口令账号，
    /// 等于给所有人留了一扇后门。
    /// </para>
    /// <para>
    /// 调用点在 <c>AppHost.InitializeAsync</c>，失败由它 catch 后只记日志：
    /// 账号初始化失败的正确后果是"没人能登录、只能看状态"，而不是"程序起不来"。
    /// 本方法自己不吞异常（仓储怎么抛就怎么抛），把降级决定留给启动流程。
    /// </para>
    /// <para>
    /// 线程：启动线程上一次性的调用，内部 <c>await</c> 后会落到线程池；
    /// 除写库外无共享状态，不会与正在跑的登录互相干扰（此时根本还没人能登录）。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 认证。**唯一**的密码校验入口，任何地方都不要绕开它直接调 <c>PasswordHasher.Verify</c>
    /// （否则失败计数、锁定与审计都会漏）。
    ///
    /// <para>
    /// 判定顺序是刻意的，每一步都在挡一类事故：
    /// 空输入 → 锁定期 → 查库（异常按失败处理）→ 账号不存在 → 已停用 → 密码错 → 成功。
    /// 锁定期放在查库之前：锁定期间连数据库都不碰，爆破就打不到库上。
    /// </para>
    /// <para>
    /// 副作用：写审计（每次尝试）、改内存失败计数、成功后更新最后登录时间、必要时升级密码轮数。
    /// 后两项失败只记日志 —— 登录本身已经成立，不该因为"记不上最后登录时间"把人挡在门外。
    /// </para>
    /// <para>
    /// 失败行为：从不抛。所有异常都翻译成
    /// <see cref="AuthOutcome"/> + 一句给操作员看的话（见 <see cref="AuthResult.Message"/>）。
    /// 线程：UI 线程发起（登录命令），await 之后在线程池上继续，返回值不再碰界面对象。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 改密。前置条件：知道当前密码（**所以它不是"重置密码"** —— 管理员重置这条路径目前不存在）。
    ///
    /// <para>
    /// 四道校验按顺序：账号存在 → 当前密码正确 → 新密码够长 → 新旧不同。
    /// "新旧必须不同"不是洁癖：出厂口令登录 → 改密成同一个串 → 强制改密标志被清掉，
    /// 等于没改。
    /// </para>
    /// <para>
    /// 成功后把 <c>MustChangePassword</c> 置 false，这就是"首次登录强制改密"的出口 ——
    /// 标志位的清除发生在改密成功这一刻，而不是登录的时候，否则跳过改密也能进主界面。
    /// </para>
    /// <para>
    /// 失败行为：返回 <c>(false, 原因)</c>，原因直接给操作员看；不抛（除了仓储自身的异常，
    /// 那属于真故障，由界面统一按"操作失败"提示）。
    /// 注意本方法<b>不检查 <c>IsEnabled</c></b>：停用账号拿着旧密码仍可改密，
    /// 但它照样登不进来（<see cref="AuthenticateAsync"/> 会拦），所以不构成绕过。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 列出全部账号（用户管理页用）。纯转发、不做过滤 ——
    /// 停用账号要留在列表里，否则"这个人曾经存在"就查不到了。
    /// 失败（读库异常）直接抛给调用方，这里没有可降级的返回值。
    /// </summary>
    public Task<IReadOnlyList<UserAccount>> GetUsersAsync(CancellationToken ct = default)
        => _repo.GetAllAsync();

    // ── 用户管理（F-64） ──

    /// <summary>
    /// 新建账号。三道校验的顺序是刻意的：用户名 → 角色 → 密码 → 撞名。
    /// 撞名放在最后，是因为它要查库；前三项纯内存判断，能不出门就不出门。
    ///
    /// <para>
    /// <b>撞名判定必须走 <see cref="IUserRepository.FindAsync"/></b>：它大小写不敏感，
    /// 与登录口径一致。直接靠主键撞库会抛 SQLite 的约束异常，
    /// 界面拿到的就是一句英文堆栈 —— 而"admin 已存在"才是操作员能处理的信息。
    /// </para>
    /// <para>
    /// <b><c>MustChangePassword = true</c> 是硬要求</b>：初始密码是管理员口头/写在交接班表上的，
    /// 知道它的人可能不止一个人。不强制改密，这个号就永远是一个公共账号。
    /// </para>
    /// <para>
    /// 线程：UI 线程发起（按钮命令），内部两次 await 之后落到线程池；
    /// 失败行为：业务失败返回 <see cref="UserAdminResult.Fail"/>，数据库异常照常抛给调用方
    /// （那是真故障，界面统一按"操作失败"提示并记审计 Failed）。
    /// </para>
    /// </summary>
    public async Task<UserAdminResult> CreateUserAsync(
        string userName, UserRole role, string initialPassword, string actor,
        CancellationToken ct = default)
    {
        var name = (userName ?? string.Empty).Trim();

        if (name.Length == 0) return UserAdminResult.Fail("用户名不能为空");
        if (name.Any(char.IsWhiteSpace)) return UserAdminResult.Fail("用户名不能包含空格");
        if (!Enum.IsDefined(role)) return UserAdminResult.Fail("角色无效");

        if (string.IsNullOrEmpty(initialPassword) || initialPassword.Length < MinPasswordLength)
            return UserAdminResult.Fail($"初始密码至少 {MinPasswordLength} 位");

        // 大小写不敏感查重：与 FindAsync/登录同一口径，避免 admin 与 Admin 并存
        var existing = await _repo.FindAsync(name).ConfigureAwait(false);
        if (existing is not null)
            return UserAdminResult.Fail($"账号「{existing.UserName}」已存在（用户名不区分大小写）");

        var pwd = PasswordHasher.Create(initialPassword);

        await _repo.UpsertAsync(new UserAccount
        {
            UserName = name,
            DisplayName = name,
            Role = role,
            PasswordHash = pwd.Hash,
            PasswordSalt = pwd.Salt,
            PasswordIterations = pwd.Iterations,
            IsEnabled = true,
            MustChangePassword = true,
            CreatedAt = _now(),
        }).ConfigureAwait(false);

        _logger?.LogWarning("[用户] {Actor} 新建账号 {User}（{Role}），首次登录必须改密",
            DescribeActor(actor), name, RolePermissions.Describe(role));

        return UserAdminResult.Success;
    }

    /// <summary>
    /// 启用 / 停用。停用是**唯一**的"删人"手段（仓储刻意没有 Delete，理由见
    /// <see cref="IUserRepository"/>），所以这里的两条自保护就是全部的防线。
    ///
    /// <para>
    /// 幂等：账号已经是目标状态时直接返回成功，不写库、不报错 ——
    /// 界面上连点两下"停用"不该看到第二句"操作失败"。
    /// </para>
    /// <para>
    /// <b>两条底线，顺序不能反</b>：先拦"停用自己"（操作员最常误点的就是自己那一行），
    /// 再拦"最后一个启用中的管理员"。后者查的是<b>库里现在的状态</b>，
    /// 不是在内存里推算 —— 提前算会漏掉"另一个人刚被停用"的情况。
    /// </para>
    /// </summary>
    public async Task<UserAdminResult> SetUserEnabledAsync(
        string userName, bool enabled, string actor, CancellationToken ct = default)
    {
        var user = await _repo.FindAsync(userName).ConfigureAwait(false);
        if (user is null) return UserAdminResult.Fail("账号不存在");

        // 已经是这个状态：什么都不用做，也别记成失败
        if (user.IsEnabled == enabled) return UserAdminResult.Success;

        if (!enabled)
        {
            if (IsSelf(user, actor))
                return UserAdminResult.Fail("不能停用当前登录的账号");

            if (await IsLastEnabledAdminAsync(user).ConfigureAwait(false))
                return UserAdminResult.Fail("这是最后一个启用中的管理员，停用后就没人能管理账号了");
        }

        // 仓储这里不返回 bool：目标账号刚刚才查到，找不到只可能是并发删表这种真故障
        await _repo.SetEnabledAsync(user.UserName, enabled).ConfigureAwait(false);

        _logger?.LogWarning("[用户] {Actor} {Action}账号 {User}",
            DescribeActor(actor), enabled ? "启用" : "停用", user.UserName);

        return UserAdminResult.Success;
    }

    /// <summary>
    /// 管理员重置他人密码：不需要旧密码，直接把新密码写进去，并置
    /// <c>MustChangePassword = true</c>。
    ///
    /// <para>
    /// <b>为什么必须强制改密</b>：管理员知道这个临时密码，如果不强制改，
    /// 管理员可以随时用这串密码登进这个号 —— 那就等于每个账号都长了一样。
    /// 强制改密把"临时密码的知晓期"限制到本人下一次登录为止。
    /// </para>
    /// <para>
    /// 与 <see cref="ChangePasswordAsync"/> 的差别：那个要旧密码、清掉改密标志
    /// （本人改完就算完）；这个不要旧密码、设上改密标志。两条路径不能合并 ——
    /// 合并的第一种写法就是"管理员重置也校验旧密码"，等于重置不了。
    /// </para>
    /// <para>
    /// 允许重置自己：管理员忘了密码时现场只有这台机器，不给这条路就只能重装。
    /// 代价是自己下一次登录也会被要求改密，这是可接受的。
    /// </para>
    /// </summary>
    public async Task<UserAdminResult> ResetPasswordAsync(
        string userName, string newPassword, string actor, CancellationToken ct = default)
    {
        var user = await _repo.FindAsync(userName).ConfigureAwait(false);
        if (user is null) return UserAdminResult.Fail("账号不存在");

        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < MinPasswordLength)
            return UserAdminResult.Fail($"新密码至少 {MinPasswordLength} 位");

        var fresh = PasswordHasher.Create(newPassword);

        // mustChangePassword: true —— 重置出来的密码只是"临时通行证"
        await _repo.UpdatePasswordAsync(
            user.UserName, fresh.Hash, fresh.Salt, fresh.Iterations, mustChangePassword: true)
            .ConfigureAwait(false);

        _logger?.LogWarning("[用户] {Actor} 重置了账号 {User} 的密码（下次登录必须修改）",
            DescribeActor(actor), user.UserName);

        return UserAdminResult.Success;
    }

    /// <summary>
    /// 改角色。提权与降权走同一条路径（审计里用 Before/After 记方向）。
    ///
    /// <para>
    /// <b>为什么降权也要查"最后一个管理员"</b>：把唯一的管理员改成工程师，
    /// 后果和停用他完全一样 —— 系统里再没有人有 <c>ManageUsers</c>，
    /// 连"改回来"都做不到。拦的是后果，不是动作名字。
    /// </para>
    /// <para>
    /// 允许改自己的角色（前提是改完仍有别的启用管理员）：现场一个管理员
    /// 要把权限交出去时，先建新管理员再降自己是最正常的流程。
    /// 注意界面上的会话不会因此变化 —— 角色要重新登录才生效（<see cref="ISessionService"/> 的设计）。
    /// </para>
    /// </summary>
    public async Task<UserAdminResult> ChangeRoleAsync(
        string userName, UserRole role, string actor, CancellationToken ct = default)
    {
        var user = await _repo.FindAsync(userName).ConfigureAwait(false);
        if (user is null) return UserAdminResult.Fail("账号不存在");
        if (!Enum.IsDefined(role)) return UserAdminResult.Fail("角色无效");

        // 角色没变：不写库、不记失败（界面上的"保存"经常有人连点）
        if (user.Role == role) return UserAdminResult.Success;

        if (user.Role == UserRole.Admin && role != UserRole.Admin
            && await IsLastEnabledAdminAsync(user).ConfigureAwait(false))
            return UserAdminResult.Fail("这是最后一个启用中的管理员，改成其他角色后就没人能管理账号了");

        if (!await _repo.SetRoleAsync(user.UserName, role).ConfigureAwait(false))
            return UserAdminResult.Fail("账号不存在");

        _logger?.LogWarning("[用户] {Actor} 修改账号 {User} 的角色：{Before} → {After}",
            DescribeActor(actor), user.UserName,
            RolePermissions.Describe(user.Role), RolePermissions.Describe(role));

        return UserAdminResult.Success;
    }

    /// <summary>
    /// 目标账号是不是当前操作人自己。大小写不敏感（与查库口径一致）：
    /// 操作员在小键盘上敲出的 "Admin" 和会话里的 "admin" 是同一个人。
    /// </summary>
    private static bool IsSelf(UserAccount user, string actor)
        => !string.IsNullOrWhiteSpace(actor)
           && string.Equals(user.UserName, actor.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 目标账号是不是"最后一个启用中的管理员"。
    ///
    /// 判据是查库后的计数，不是缓存：停用/改角色都发生在用户管理页，
    /// 而管理员可能刚刚在另一台机器上改过（同一台机器上也可能连着点两下）。
    /// 账号表只有个位数行，多查一次不值得为它引入缓存失效逻辑。
    ///
    /// <para>
    /// <b>已知边界</b>：查计数与写库是两次独立操作，中间有人并发停用另一个管理员时
    /// 仍可能双双通过。这个程序里管理动作只有一个人在一个界面上做（单机上位机），
    /// 不为此引入事务与重试。
    /// </para>
    /// </summary>
    private async Task<bool> IsLastEnabledAdminAsync(UserAccount user)
    {
        // 停用的管理员本来就不能登录，不构成"锁死"；非管理员更无关
        if (user.Role != UserRole.Admin || !user.IsEnabled) return false;

        var all = await _repo.GetAllAsync().ConfigureAwait(false);
        return all.Count(u => u.IsEnabled && u.Role == UserRole.Admin) <= 1;
    }

    /// <summary>日志里的操作人。取不到就写 unknown —— 不因为少一个名字丢掉整行日志。</summary>
    private static string DescribeActor(string actor)
        => string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim();

    // ── 失败计数（内存，见类注释第 1 条） ──

    /// <summary>
    /// 是否处于锁定期；是则带出剩余时间（用于提示"请 X 秒后再试"）。
    ///
    /// 顺手做过期清零：不在这里清，锁定到期后的第一次失败会因为 Count 仍 ≥ 阈值而立刻再锁，
    /// 表现成"输对密码也进不来"。这一步必须在锁内做，否则并发下会重复清零/重复锁定。
    /// </summary>
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

    /// <summary>
    /// 清掉某账号的失败计数。只在**密码验证通过**时调 ——
    /// 放在"账号存在"那一支会把"用户名对、密码错"的前两次白算。
    /// </summary>
    private void ClearFailures(string userName)
    {
        lock (_gate)
        {
            _failures.Remove(userName);
        }
    }

    /// <summary>
    /// 刚好触发锁定时补两条记录（审计 + 日志）。单独一个方法是为了让
    /// "账号不存在"和"密码错"两条分支都走同一段，避免只在一处记、另一处漏。
    /// </summary>
    private void RecordLock(string userName)
    {
        _audit.RecordAs(userName, "未登录", AuditAction.LoginLocked, userName,
            result: AuditResult.Denied,
            detail: $"连续失败 {MaxFailuresBeforeLock} 次，锁定 {(int)LockDuration.TotalSeconds} 秒");
        _logger?.LogWarning("[用户] {User} 连续失败 {Count} 次，已锁定 {Seconds} 秒",
            userName, MaxFailuresBeforeLock, (int)LockDuration.TotalSeconds);
    }
}
