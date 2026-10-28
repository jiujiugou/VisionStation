namespace VisionStation.Core.Models;

/// <summary>
/// 审计动作。只列本项目真会发生的动作 —— 不预留"将来可能用到"的枚举值。
///
/// <para>
/// <b>为什么不用字符串</b>：动作是受约束的集合，字符串写错永远不会有人发现
/// （"RecipeSwitch" 和 "RecipeSwtich" 能同时存在于库里，统计时互相不相认）。
/// 存成 int 还有个副作用好处：按动作做索引很小。
/// </para>
///
/// <para>
/// <b>数值按十位分段</b>：0~1 程序生命周期，10~13 设备操作，20~23 配置变更，
/// 30~34 会话，40~45 账号管理。查库时按段位筛就能分出"人的操作"和"系统的动作"。
/// </para>
///
/// <para>
/// <b><see cref="EStopReset"/> 的现状（2026-10-28 F-47 起）</b>：控制面板有了真实的
/// 「复位急停」动作（<c>ControlPanelViewModel.ResetEStop</c>），每次复位记一条本值。
/// 它仍<b>不受权限位保护</b>（<c>RolePermissions.RequiresPermission</c> 走 default → null）——
/// 刻意的：复位是恢复生产的安全动作，权限门槛越高，被锁在场外的时间越长；
/// 门槛由"必须人工按一次"这个物理动作承担，不由账号承担。
/// </para>
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

    /// <summary>
    /// 复位急停。
    ///
    /// <b>与实现不符（原注释写"只有管理员能做"）</b>：<c>RolePermissions.RequiresPermission</c>
    /// 里没有这一项（走 default → null），全仓也没有任何地方记过这个动作
    /// （对照 <see cref="EStopPressed"/> 是有埋点的）。所以它是一个只在枚举与显示文案里
    /// 存在的预留值 —— 别据此认为复位急停受管理员权限保护。
    /// </summary>
    EStopReset = 13,

    /// <summary>切换运行/工程师模式</summary>
    ModeSwitch = 20,

    /// <summary>
    /// 重新加载引擎（VM 方案重载 / 换引擎）。
    ///
    /// <b>2026-10-27 起没有任何埋点</b>：「重载流程」按钮已随 <c>IInspectionEngine.Reload()</c>
    /// 一起删除（VM 模式下流程归平台，本程序没有可重载的资源）。
    /// 枚举值保留 —— 老库里的历史记录还叫这个名，删掉会让 <c>AuditActionText.Of</c>
    /// 把那些行显示成未知动作。
    /// </summary>
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

    /// <summary>修改密码（本人改自己的，需要知道旧密码）</summary>
    PasswordChange = 42,

    /// <summary>
    /// 启用用户。
    ///
    /// <para>
    /// 与 <see cref="UserDisable"/> 分开而不是"用同一个动作 + detail 说明"：
    /// 审计页按动作筛选，"谁被停用过"和"谁被放回来过"是两条完全不同的追问，
    /// 挤在一个枚举值里就只能靠读 detail 文本才能分开。
    /// </para>
    /// </summary>
    UserEnable = 43,

    /// <summary>
    /// 修改用户角色。
    ///
    /// 提权与降权都记这一个动作，前后值记在 <c>Before/After</c>（角色中文名）——
    /// 动作类型不该随方向裂成两个值，否则加一级角色就要再加两个枚举。
    /// </summary>
    UserRoleChange = 44,

    /// <summary>
    /// 管理员重置他人密码（不知道旧密码，重置后强制本人改密）。
    ///
    /// 与 <see cref="PasswordChange"/> 分开：后者是"本人凭旧密码改密"，
    /// 权限表里明确不需要任何权限；重置是管理动作，必须由 <c>ManageUsers</c> 把关。
    /// 混成一个值会让审计页分不清"他改了自己的密码"和"别人把他密码重置了"。
    /// </summary>
    PasswordReset = 45,
}

/// <summary>
/// 审计结果。三态而不是两态：
///
/// <para>
/// <see cref="Denied"/> 是"被权限拒绝"——这条比十次成功操作更值得看，
/// 因为它说明有人在试。只记成功等于把最该看的记录删了。
/// </para>
/// <para>
/// <b>三个值不是"成功/失败"的粗分</b>：<see cref="Denied"/> 是权限系统挡下来的（业务正常，
/// 但值得追问"谁在按"）；<see cref="Failed"/> 是动作本身没做成（设备拒绝、IO 异常）。
/// 混用会让审计页上分不清"有人试图越权"和"按钮按了但设备没响应"。
/// </para>
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
/// 一条审计记录 —— "谁、什么时候、动了什么、改前改后是什么、成没成"。
///
/// <para>
/// <b>定位</b>：它是审计链路的通用货币。上游是 <c>AuditService.Record / RecordAs</c>
/// （在按钮命令里被调用，拿到当前会话身份后组装），下游是
/// <c>AuditRepository.SaveAsync</c>（转成 <c>AuditLogRecord</c> 落进 SQLite）
/// 与 <c>AuditPageViewModel</c>（查出来给人看）。本类不认识 EF，也不认识界面。
/// </para>
///
/// <para>
/// <b>归属与线程</b>：每次记录 new 一个，<b>不可变</b>（全部 <c>init</c>）；
/// 在调用方线程（通常是 UI 线程的按钮命令）组装，然后丢进有界队列，
/// 由后台消费线程读走写库。之所以做成不可变快照而不是"引用当前会话"：
/// 队列里滞留的几百毫秒内用户可能已经登出，写库时再去读会话会记错人。
/// </para>
///
/// <para>
/// <b>失败行为</b>：纯数据，不抛异常。真正可能失败的是落库，而那一步
/// <c>AuditService</c> 只记日志、丢最旧/最新一条，<b>绝不反向影响业务</b> ——
/// 审计写不进去是次要问题，卡住"停止检测"是主要事故。
/// </para>
///
/// <para>
/// 字段来自工业现场的硬要求（天天PLC 的 HMI 十条原则原话）：
/// "必须记录谁、什么时间、做了什么操作、修改前的值和修改后的值"。
/// 记"操作员改了参数"在质量事故追责时毫无用处；记
/// "阈值 0.5 → 0.65，操作员 A，14:32" 才能定责。
/// </para>
///
/// <para>
/// <see cref="UserName"/> / <see cref="UserRole"/> 为什么是 string 而不是枚举：
/// 审计是"记录当时的事实"，历史记录不该因为将来枚举改名而失真。
/// 未登录的动作（启动、登录失败）用户名取 Windows 账户，角色记"未登录"；
/// 身份取不到时两者都记 "unknown"，但<b>记录本身照写</b> ——
/// "不知道是谁"也是信息，丢掉这条就没法证明动作发生过。
/// </para>
/// </summary>
public sealed record AuditEntry
{
    /// <summary>
    /// 库内自增主键；写入前为 0。
    ///
    /// 不是 Guid：审计是纯追加流，自增 ID 本身就是"第几条"，按 Id 倒序就是时间倒序
    /// （查询用的正是 <c>OrderByDescending(Id)</c>）。也省一半存储 —— 这张表按年攒。
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// 发生时刻（本机时间，与检测记录同一时钟语义）。
    /// 本表没有单独的入库时间列：审计的"发生"和"记录"只差一次队列投递（最多几百毫秒）。
    /// 保留期若将来接上，也按这一列删。
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>操作人。**是快照不是外键**：账号停用/改名后，历史记录仍然是当时那个名字。</summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>
    /// 操作人角色（Operator / Technician / Engineer / Admin / 未登录 / unknown）。
    /// 存文本而不是 <see cref="UserRole"/> 枚举：这是"当时是什么角色"的留痕，
    /// 不该随角色定义变化而改变含义。
    /// </summary>
    public string UserRole { get; init; } = string.Empty;

    /// <summary>
    /// 做了什么。
    /// <b>EF 把它按 int 存</b> —— 枚举成员只能往后追加，不能删、不能改数值，
    /// 否则老记录的含义会跟着新代码一起变，而库里没有任何东西能发现这件事。
    /// </summary>
    public AuditAction Action { get; init; }

    /// <summary>
    /// 操作对象：配方名 / 参数名 / 被操作用户名。
    /// 不适用时留空 —— 但"启动检测"这类动作至少应该填上当前配方名，
    /// 否则事后无法回答"这班是谁在用哪份配方"。
    /// </summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>
    /// 修改前的值。只对"修改类"动作有意义，必须记值不记描述。
    /// 用 null 而不是空串表示"这件事没有前后值"（登录、启动）——
    /// 写空串会让"没有值"和"值就是空"混在一起。
    /// </summary>
    public string? Before { get; init; }

    /// <summary>修改后的值。同上；<c>Before/After</c> 要成对出现才算一条可复盘的修改记录。</summary>
    public string? After { get; init; }

    /// <summary>结果：成功 / 被拒绝 / 失败（见 <see cref="AuditResult"/> 的三态说明）</summary>
    public AuditResult Result { get; init; } = AuditResult.Success;

    /// <summary>
    /// 补充说明（异常信息、失败原因）。不用来装 JSON 大对象 ——
    /// 库里有长度上限，超长会被截断，而截断后的 JSON 比没有更误导。
    /// </summary>
    public string? Detail { get; init; }
}
