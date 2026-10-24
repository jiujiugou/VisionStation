using VisionStation.Core.Users;

namespace VisionStation.Service.Security;

/// <summary>会话发生了什么变化</summary>
public enum SessionChangeKind
{
    /// <summary>登录成功</summary>
    Login,

    /// <summary>登出（主动或超时）</summary>
    Logout,

    /// <summary>无操作降级（角色变低，但仍处于登录态）</summary>
    Downgraded,
}

/// <summary>
/// 会话变化的通知内容。
///
/// <see cref="Reason"/> 是有意带出来的：登出的原因（主动 / 无操作 / 班次超时）
/// 决定了审计里记什么，也决定了界面上该说"您已退出"还是"因超时降为操作员"。
/// </summary>
public sealed class SessionChangedEventArgs : EventArgs
{
    public SessionChangeKind Kind { get; init; }

    /// <summary>变化后的当前账号；登出后为 null</summary>
    public UserAccount? User { get; init; }

    /// <summary>
    /// 变化前的账号。登出事件里 <see cref="User"/> 已经是 null，
    /// 但审计必须记下"是谁被登出的" —— 否则超时登出这类记录会变成无主账。
    /// </summary>
    public UserAccount? PreviousUser { get; init; }

    /// <summary>变化后的有效角色</summary>
    public UserRole EffectiveRole { get; init; }

    /// <summary>人话原因</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// 是否由超时机制自动触发（而非用户主动操作）。
    ///
    /// 主动登出的审计由发起命令记，自动超时的审计由订阅方记 ——
    /// 没有这个标记，两边要么重复记两条，要么全都不记。
    /// </summary>
    public bool IsAutomatic { get; init; }
}

/// <summary>
/// 当前会话：谁登录着、还剩多少权限、什么时候该收回来。
///
/// 为什么不把"当前用户"塞进 <see cref="IUserService"/>：
/// 认证是一次性动作，会话是持续状态。分开放，测"5 分钟无操作会降级"时
/// 不需要构造一次真实的密码验证。
/// </summary>
public interface ISessionService
{
    /// <summary>当前登录的账号；未登录为 null</summary>
    UserAccount? CurrentUser { get; }

    /// <summary>
    /// 有效角色 —— 降级后它会低于账号本身的角色。
    ///
    /// 所有权限判断都必须走这个属性，不能直接看 <see cref="UserAccount.Role"/>，
    /// 否则降级就形同虚设。
    /// </summary>
    UserRole EffectiveRole { get; }

    bool IsAuthenticated { get; }

    /// <summary>登录成功后由调用方触发</summary>
    void Start(UserAccount user);

    /// <summary>主动登出</summary>
    void Logout(string reason = "主动退出");

    /// <summary>有操作时调用，重置无操作计时</summary>
    void Touch();

    /// <summary>是否拥有某权限（未登录一律 false）</summary>
    bool HasPermission(Permission permission);

    event EventHandler<SessionChangedEventArgs>? Changed;
}

/// <summary>
/// 会话服务。
///
/// 两个超时机制，别混：
///
///   1) <b>5 分钟无操作 → 降级为操作员</b>。人没走，只是太久没动 ——
///      比如工程师调完参数去拿工具，回来还能接着看，不用重新输密码。
///      但权限已经收回，别人这时候按不动参数。
///
///   2) <b>8 小时 → 完全登出</b>。三班制正好一个班次；换班就必须换人登录，
///      否则下一班接着用上一班的身份，追溯直接失效。
///
/// 出处：注塑机 HMI 原则给的是 3–5 分钟自动注销；电厂 iFIX 项目给的是 8 小时。
/// 本项目把两者拆成"降级"和"登出"两级 —— 直接 5 分钟踢出去，
/// 操作员一天要输几十次密码，产线不会等他。
/// </summary>
public sealed class SessionService : ISessionService, IDisposable
{
    /// <summary>无操作多久降级</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>登录多久强制登出（一个班次）</summary>
    public static readonly TimeSpan ShiftDuration = TimeSpan.FromHours(8);

    /// <summary>检查间隔</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly Func<DateTime> _now;
    private readonly ITimer? _timer;
    private readonly object _gate = new();

    private UserAccount? _user;
    private UserRole _effectiveRole = UserRole.Operator;
    private DateTime _loginAt;
    private DateTime _lastActivity;
    private int _disposed;

    /// <param name="now">时间源，测试注入用</param>
    /// <param name="startTimer">
    /// 是否启动后台检查。测试传 false 后手动调 <see cref="CheckTimeouts"/>，
    /// 否则一条断言要等 15 秒。
    /// </param>
    public SessionService(Func<DateTime>? now = null, bool startTimer = true)
    {
        _now = now ?? (() => DateTime.Now);

        if (startTimer)
        {
            _timer = new TimerAdapter(_ => CheckTimeouts(), CheckInterval, CheckInterval);
        }
    }

    public UserAccount? CurrentUser
    {
        get { lock (_gate) return _user; }
    }

    public UserRole EffectiveRole
    {
        get { lock (_gate) return _user is null ? UserRole.Operator : _effectiveRole; }
    }

    public bool IsAuthenticated
    {
        get { lock (_gate) return _user is not null; }
    }

    public event EventHandler<SessionChangedEventArgs>? Changed;

    public void Start(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);

        SessionChangedEventArgs args;
        lock (_gate)
        {
            _user = user;
            _effectiveRole = user.Role;
            var now = _now();
            _loginAt = now;
            _lastActivity = now;

            args = new SessionChangedEventArgs
            {
                Kind = SessionChangeKind.Login,
                User = user,
                EffectiveRole = _effectiveRole,
                Reason = $"登录成功（{RolePermissions.Describe(user.Role)}）",
            };
        }

        Changed?.Invoke(this, args);
    }

    public void Logout(string reason = "主动退出")
    {
        SessionChangedEventArgs? args = null;
        lock (_gate)
        {
            if (_user is null) return;

            args = new SessionChangedEventArgs
            {
                Kind = SessionChangeKind.Logout,
                User = null,
                PreviousUser = _user,
                EffectiveRole = UserRole.Operator,
                Reason = reason,
            };

            _user = null;
            _effectiveRole = UserRole.Operator;
        }

        Changed?.Invoke(this, args);
    }

    public void Touch()
    {
        lock (_gate)
        {
            if (_user is null) return;
            _lastActivity = _now();
        }
    }

    public bool HasPermission(Permission permission)
    {
        lock (_gate)
        {
            // 未登录：任何需要权限的动作都不放行（None 除外）
            if (_user is null) return permission == Permission.None;
            return RolePermissions.Has(_effectiveRole, permission);
        }
    }

    /// <summary>
    /// 检查两个超时。<see cref="ITimer"/> 每 15 秒调一次；测试可直接调。
    /// </summary>
    internal void CheckTimeouts()
    {
        SessionChangedEventArgs? downgrade = null;
        SessionChangedEventArgs? logout = null;

        lock (_gate)
        {
            if (_user is null) return;

            var now = _now();

            // 先判班次：8 小时到了就直接登出，不必再谈降级
            if (now - _loginAt >= ShiftDuration)
            {
                logout = new SessionChangedEventArgs
                {
                    Kind = SessionChangeKind.Logout,
                    User = null,
                    PreviousUser = _user,
                    EffectiveRole = UserRole.Operator,
                    IsAutomatic = true,
                    Reason = $"登录已满 {(int)ShiftDuration.TotalHours} 小时（一个班次），必须重新登录",
                };

                _user = null;
                _effectiveRole = UserRole.Operator;
            }
            else if (_effectiveRole > UserRole.Operator && now - _lastActivity >= IdleTimeout)
            {
                _effectiveRole = UserRole.Operator;

                downgrade = new SessionChangedEventArgs
                {
                    Kind = SessionChangeKind.Downgraded,
                    User = _user,
                    EffectiveRole = _effectiveRole,
                    IsAutomatic = true,
                    Reason = $"{(int)IdleTimeout.TotalMinutes} 分钟无操作，已降为操作员权限",
                };
            }
        }

        if (logout is not null) Changed?.Invoke(this, logout);
        if (downgrade is not null) Changed?.Invoke(this, downgrade);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _timer?.Dispose();
    }

    /// <summary>
    /// 定时器接口的极简抽象 —— 只为让 <see cref="SessionService"/> 不直接依赖
    /// <see cref="System.Threading.Timer"/> 的生命周期细节。
    /// </summary>
    private interface ITimer : IDisposable
    {
    }

    private sealed class TimerAdapter : ITimer
    {
        private readonly System.Threading.Timer _timer;

        public TimerAdapter(TimerCallback callback, TimeSpan due, TimeSpan period)
            => _timer = new System.Threading.Timer(callback, null, due, period);

        public void Dispose() => _timer.Dispose();
    }
}
