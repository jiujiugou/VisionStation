namespace VisionStation.Core.Models;

/// <summary>
/// 审计动作。只列本项目真会发生的动作 —— 不预留"将来可能用到"的枚举值。
///
/// 为什么不用字符串：动作是受约束的集合，字符串写错永远不会有人发现
/// （"RecipeSwitch" 和 "RecipeSwtich" 能同时存在于库里，统计时互相不相认）。
/// 存成 int 还有个副作用好处：按动作做索引很小。
/// </summary>
public enum AuditAction
{
    /// <summary>程序启动（含引擎/配方加载完成）</summary>
    AppStart = 0,

    /// <summary>程序正常退出</summary>
    AppStop = 1,

    /// <summary>开始检测</summary>
    Start = 10,

    /// <summary>停止检测</summary>
    Stop = 11,

    /// <summary>按下急停</summary>
    EStopPressed = 12,

    /// <summary>复位急停（只有管理员能做，见权限方案）</summary>
    EStopReset = 13,

    /// <summary>切换运行/工程师模式</summary>
    ModeSwitch = 20,

    /// <summary>重新加载引擎（VM 方案重载 / 换引擎）</summary>
    EngineReload = 21,

    /// <summary>切换配方（换型）</summary>
    RecipeSwitch = 22,

    /// <summary>修改参数</summary>
    ParameterChange = 23,

    /// <summary>登录成功</summary>
    Login = 30,

    /// <summary>登出（含超时自动登出）</summary>
    Logout = 31,

    /// <summary>登录失败（密码错）</summary>
    LoginFailed = 32,

    /// <summary>登录被锁（连续失败达阈值）</summary>
    LoginLocked = 33,

    /// <summary>
    /// 会话因超时被降级或强制登出。
    ///
    /// 与 <see cref="Logout"/> 分开：主动退出是人的决定，
    /// 超时是系统替你做的决定 —— 后者在追责时更需要被看见
    /// （"操作员离开工位 5 分钟，权限已自动收回"）。
    /// </summary>
    SessionExpired = 34,

    /// <summary>新建用户</summary>
    UserCreate = 40,

    /// <summary>停用用户</summary>
    UserDisable = 41,

    /// <summary>修改密码</summary>
    PasswordChange = 42,
}

/// <summary>
/// 审计结果。三态而不是两态：
///
/// <see cref="Denied"/> 是"被权限拒绝"——这条比十次成功操作更值得看，
/// 因为它说明有人在试。只记成功等于把最该看的记录删了。
/// </summary>
public enum AuditResult
{
    /// <summary>操作成功</summary>
    Success = 0,

    /// <summary>权限不足被拒绝</summary>
    Denied = 1,

    /// <summary>操作本身失败（异常、设备拒绝、IO 错误）</summary>
    Failed = 2,
}

/// <summary>
/// 一条审计记录。
///
/// 字段来自工业现场的硬要求（天天PLC 的 HMI 十条原则原话）：
/// "必须记录谁、什么时间、做了什么操作、修改前的值和修改后的值"。
/// 记"操作员改了参数"在质量事故追责时毫无用处；记
/// "阈值 0.5 → 0.65，操作员 A，14:32" 才能定责。
///
/// <para>
/// <see cref="UserName"/> / <see cref="UserRole"/> 为什么是 string 而不是枚举：
/// 审计是"记录当时的事实"，历史记录不该因为将来枚举改名而失真。
/// 阶段 1 没有登录，用户名取 Windows 账户，角色记"未登录"；
/// 阶段 2 接入会话后写入真实角色。
/// </para>
/// </summary>
public sealed record AuditEntry
{
    /// <summary>库内自增主键；写入前为 0</summary>
    public long Id { get; init; }

    /// <summary>发生时刻（本机时间，与检测记录同一时钟语义）</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>操作人</summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>操作人角色（Operator / Technician / Engineer / Admin / 未登录）</summary>
    public string UserRole { get; init; } = string.Empty;

    /// <summary>做了什么</summary>
    public AuditAction Action { get; init; }

    /// <summary>
    /// 操作对象：配方名 / 参数名 / 被操作用户名。
    /// 不适用时留空 —— 但"启动检测"这类动作至少应该填上当前配方名，
    /// 否则事后无法回答"这班是谁在用哪份配方"。
    /// </summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>修改前的值。只对"修改类"动作有意义，必须记值不记描述。</summary>
    public string? Before { get; init; }

    /// <summary>修改后的值。同上。</summary>
    public string? After { get; init; }

    /// <summary>结果：成功 / 被拒绝 / 失败</summary>
    public AuditResult Result { get; init; } = AuditResult.Success;

    /// <summary>补充说明（异常信息、失败原因）。不用来装 JSON 大对象。</summary>
    public string? Detail { get; init; }
}
