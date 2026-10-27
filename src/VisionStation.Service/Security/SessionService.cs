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
///
/// <para>
/// 角色（4 级：操作员/技术员/工程师/管理员）与权限位（12 个 + None，共 13 项枚举）
/// 的映射表在 Core 的 <c>RolePermissions</c> 里，本接口不复制一份 ——
/// "操作员能不能改配方"这个问题只能有一个答案。这里只负责<b>用有效角色去查表</b>。
/// </para>
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
///
/// <para>
/// <b>定位</b>：认证之后的"持续状态"。认证（<see cref="IUserService"/>）回答"密码对不对"，
/// 本类回答"现在算谁、还剩什么权限、什么时候收回去"。
/// 上层有三类消费者：界面 VM 读 <see cref="CurrentUser"/> / <see cref="HasPermission"/>；
/// <c>SessionBridge</c> 订阅 <see cref="Changed"/> 把事件搬到 UI 线程并补记超时审计；
/// <c>AuditService</c> 通过注入的委托读 <see cref="CurrentUser"/> 决定"这条审计算谁的"。
/// </para>
///
/// <para>
/// <b>归属</b>：DI 单例（<c>ServiceCollectionExtensions</c> 里 <c>new SessionService()</c>），
/// 活到进程结束。构造即起一个 <see cref="System.Threading.Timer"/>，每 15 秒在线程池上跑一次
/// <see cref="CheckTimeouts"/> —— 也就是说超时判定**不在 UI 线程上发生**。
/// 因此 <see cref="Changed"/> 的订阅方必须自己处理线程切换（<c>SessionBridge</c> 用
/// Dispatcher.BeginInvoke，这是它存在的第一个理由）。所有状态字段由 <c>_gate</c> 保护，
/// 读属性也加锁：UI 线程读、线程池写，不加锁就是数据竞争。
/// </para>
///
/// <para>
/// <b>失败行为</b>：业务路径上从不抛（<see cref="Start"/> 对 null 账号有 <c>ThrowIfNull</c>，
/// 那属于编程错误，不是运行期失败）。登录/登出/降级都只是改内存状态并触发事件；
/// 但事件处理器若抛异常，会一路冒到定时器回调里 —— 那里没有任何 try，
/// 在 .NET Core 上就是"线程池线程未处理异常 = 进程退出"。
/// 所以订阅方必须自己兜住（<c>SessionBridge</c> 的 Publish 整段包了 try）。
/// </para>
///
/// <para>
/// <b>"工程师模式"与"权限位"是两个维度，别混：</b>
/// 权限位（<see cref="Permission"/>）回答"能不能做这件事"，由本类在每次
/// <see cref="HasPermission"/> 时按<b>有效角色</b>判定，是真正的闸门；
/// 工程师模式只是一个<b>界面可见性开关</b>（侧边栏多出几项、控制面板展开维护区），
/// 它自己不拦任何动作。
/// 两者必须联动，否则就出现"能看见就能点"：降级后权限已经收回，
/// 但若工程师模式还开着，侧栏的工程师入口仍在、维护区按钮仍可点，
/// 用户点下去才在更深一层被拒 —— 那种"点了没反应"的界面比拒绝更糟，
/// 而且给了越权尝试的入口。
/// 所以降级/登出时界面必须连带退出工程师模式：这件事由
/// <c>TopBarViewModel.ApplySession</c> 在做（<c>IsEngineerMode &amp;&amp; !HasPermission(EnterEngineerMode)</c> → 退出），
/// 本类只负责把"角色变了"这件事通知出去 —— **通知漏了，界面就收不回来**。
/// </para>
/// </summary>
public sealed class SessionService : ISessionService, IDisposable
{
    /// <summary>无操作多久降级</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>登录多久强制登出（一个班次）</summary>
    public static readonly TimeSpan ShiftDuration = TimeSpan.FromHours(8);

    /// <summary>检查间隔</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    /// <summary>时间源。测试注入假时钟，否则"5 分钟"这类断言只能靠真等。</summary>
    private readonly Func<DateTime> _now;

    /// <summary>后台超时检查器；测试传 startTimer:false 时为 null（改成手动调 CheckTimeouts）。</summary>
    private readonly ITimer? _timer;

    /// <summary>状态闸门。UI 线程读、线程池写，所以读属性也加锁 —— 不是"反正只有一处会改"。</summary>
    private readonly object _gate = new();

    /// <summary>当前登录账号；null = 未登录。降级时不清空（人还在，只是权限降了）。</summary>
    private UserAccount? _user;

    /// <summary>有效角色。降级就是把它压到 Operator；所有权限判断只认它，不认账号本身的 Role。</summary>
    private UserRole _effectiveRole = UserRole.Operator;

    /// <summary>本次登录时刻。班次 8 小时的计时起点，不随降级/操作刷新。</summary>
    private DateTime _loginAt;

    /// <summary>最后一次操作时刻。降级计时起点，由 <see cref="Touch"/> 刷新。</summary>
    private DateTime _lastActivity;

    /// <summary>1 = 已释放。用 Interlocked 保证 Dispose 幂等（容器关闭 + 显式调用可能各来一次）。</summary>
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

    /// <summary>
    /// 有效角色。未登录时返回 <see cref="UserRole.Operator"/> 而不是 null/异常 ——
    /// 读它的地方都是"拿来比较或显示"，给一个具体值最省事；
    /// 真正的闸门是 <see cref="HasPermission"/> 与 <see cref="IsAuthenticated"/>，
    /// 别用"角色是不是 Operator"反推有没有登录（未登录与降级后的操作员在这个值上无法区分）。
    /// </summary>
    public UserRole EffectiveRole
    {
        get { lock (_gate) return _user is null ? UserRole.Operator : _effectiveRole; }
    }

    public bool IsAuthenticated
    {
        get { lock (_gate) return _user is not null; }
    }

    public event EventHandler<SessionChangedEventArgs>? Changed;

    /// <summary>
    /// 建立会话。前置条件：调用方已经验过密码（本类不碰密码）。
    ///
    /// 副作用：覆盖当前会话（已有登录会被直接顶掉，不产生 Logout 事件）、
    /// 重置降级与班次两个计时起点、在锁**外**触发 <see cref="Changed"/>。
    /// 事件必须在锁外触发：订阅方（界面桥）可能反过来读 <see cref="CurrentUser"/>，
    /// 在锁内调外部代码就是给自己埋一个死锁。
    /// 线程：调用方在 UI 线程（登录命令里），但本方法本身线程无关。
    /// </summary>
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

    /// <summary>
    /// 主动登出。未登录时**静默返回**（不触发事件）—— 重复点"退出"不该产生一串空审计。
    ///
    /// 事件里的 <see cref="SessionChangedEventArgs.PreviousUser"/> 带的是刚才那个人：
    /// 登出后 <see cref="CurrentUser"/> 已是 null，不在这儿留一份，审计就变成无主账。
    /// 与超时登出的区别只在 <c>IsAutomatic</c>：主动登出的审计由发起命令记，超时的由桥记。
    /// </summary>
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

    /// <summary>
    /// 报告"有人操作了"，重置无操作计时。
    ///
    /// 只刷新 <c>_lastActivity</c>，**不会把已降级的角色升回去** ——
    /// 降级之后要恢复工程师权限必须重新登录，否则"随便动一下鼠标"就能绕过回收。
    /// 未登录时是空操作（不该因为界面上有人按了按钮就凭空产生会话）。
    /// </summary>
    public void Touch()
    {
        lock (_gate)
        {
            if (_user is null) return;
            _lastActivity = _now();
        }
    }

    /// <summary>
    /// 是否拥有某权限。这是**唯一**的判定入口 —— 界面绑定、命令守卫都必须走它，
    /// 直接读 <c>CurrentUser.Role</c> 会让降级形同虚设（账号角色没变，有效角色变了）。
    ///
    /// 未登录时只有 <see cref="Permission.None"/> 放行（"不需要权限"的动作才允许匿名做，
    /// 例如急停/停止：安全逻辑上不能因为没登录就停不下设备）。
    /// 线程：任意线程；内部加锁，可以安全地在后台线程上问。
    /// </summary>
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
    /// 检查两个超时。<see cref="ITimer"/> 每 15 秒调一次（线程池线程）；测试可直接调。
    ///
    /// <para>
    /// 两个 if 的顺序就是优先级：班次到了直接登出，不再谈降级 ——
    /// 否则会先发一条"降为操作员"再发一条"请重新登录"，同一秒里两条自相矛盾的通知。
    /// </para>
    /// <para>
    /// 降级只会发生一次：条件里带 <c>_effectiveRole &gt; Operator</c>，
    /// 降完就不再成立，所以不会每 15 秒刷一条"已降级"。要恢复权限必须重新登录（见 <see cref="Touch"/>）。
    /// </para>
    /// <para>
    /// 事件在锁**外**触发（与 Start/Logout 同一规矩）。这里是后台线程 ——
    /// 订阅方若直接改界面绑定就会抛跨线程异常，这正是 <c>SessionBridge</c> 存在的理由。
    /// </para>
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

    /// <summary>
    /// 停掉后台检查器。幂等：容器释放与显式调用可能各来一次。
    ///
    /// 只停 Timer、不清会话状态 —— 关闭流程里没有谁还会读它，
    /// 而"关闭时把用户登出"会多出一条没人要的审计。
    /// </summary>
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
