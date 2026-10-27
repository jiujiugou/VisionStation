namespace VisionStation.Core.Users;

/// <summary>
/// 用户角色。四级，权限累加继承。
///
/// <para>
/// <b>定位</b>：它是"人能不能干这件事"的答案来源，被 <c>SessionService</c>（按有效角色判权限）、
/// <c>UserAccount</c>（角色 → 权限位）、审计（角色写成人话）共同使用。
/// 本文件不含数据库、不含界面，是一组纯函数。
/// </para>
///
/// <para>
/// <b>为什么是四级而不是"管理员/普通用户"两级</b>：
/// 产线上这几类人干的活不一样，混成两级就必然出现"操作员也能改配方"。
/// 四级对应四件不同的事 —— 开机停机（操作员）、换型号（技术员）、
/// 调参数（工程师）、管人和查账（管理员）。
/// </para>
///
/// <para>
/// <b>关键判断：角色 / 权限位 / 工程师模式是三个东西，别混</b>
/// <list type="bullet">
///   <item><b>角色</b>（本枚举）是人的身份，登录时确定；会话超时会降级，降级后
///         <c>SessionService.EffectiveRole</c> 低于账号本身的 <c>UserAccount.Role</c>。</item>
///   <item><b>权限位</b>（<see cref="Permission"/>）是动作的粒度。判权限一律用
///         <c>SessionService.HasPermission(...)</c>，它看的是<b>有效角色</b>；
///         直接读 <c>UserAccount.Role</c> 会让超时降级形同虚设。</item>
///   <item><b>工程师模式</b>不是角色，是一个界面开关（<c>TopBarViewModel.IsEngineerMode</c>）：
///         打开后相机页/日志/控制面板维护区才出现。进它需要
///         <see cref="Permission.EnterEngineerMode"/> 这一位，退出不需要 ——
///         降级或换低权限账号时，界面会强制把模式退掉。把模式当成权限会让
///         "切了模式就等于提权"，那是真实的漏洞。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>失败行为</b>：纯函数，不抛异常。未知角色一律映射成 <see cref="Permission.None"/>，
/// <see cref="RolePermissions.Of"/> 不放大权限 —— 枚举反序列化出意外值时，
/// 默认拒绝比默认放行安全。
/// </para>
///
/// <para>
/// 出处：注塑机 HMI 改造的十条原则给出的正是这套分组
/// （操作员 / 班组长 / 设备工程师 / 管理员），本项目的 Technician 对应"班组长"。
/// </para>
/// </summary>
public enum UserRole
{
    /// <summary>操作员：启停、看状态、报警确认、查历史</summary>
    Operator = 0,

    /// <summary>技术员：操作员的全部 + 切配方、看统计</summary>
    Technician = 1,

    /// <summary>工程师：技术员的全部 + 改参数、进工程师模式、图像来源设置、编辑配方</summary>
    Engineer = 2,

    /// <summary>管理员：全部 + 用户管理、审计查询</summary>
    Admin = 3,
}

/// <summary>
/// 权限位（12 位，连同 <see cref="None"/> 共 13 个枚举项）。
/// 用 <c>[Flags]</c> 是为了"角色 = 若干权限的并集"这个语义能被累加表达。
///
/// <para>
/// <b>只列本项目真会拦的动作。</b>没有"复位急停"这一位 —— 当前程序里急停只是停触发源，
/// 没有"复位"这个动作，为一个不存在的按钮加权限位，等于给自己留一个永远为真的判断。
/// </para>
///
/// <para>
/// <b>现状（读代码时别假设每个位都在生效）</b>：真正被
/// <c>HasPermission</c> 检查过的只有 <see cref="ViewHistory"/>、<see cref="ViewAudit"/>、
/// <see cref="EnterEngineerMode"/>；其余位在当前界面里还没有对应的拦截点。
/// 其中 <see cref="CameraSettings"/> <b>已经没有任何消费者</b> ——
/// 相机层已整体删除（相机归 VisionMaster 独占），这一位只剩"发给工程师"的声明意义。
/// 加新权限位时，请同时找到它的拦截点，否则它只是文档。
/// </para>
/// </summary>
[Flags]
public enum Permission
{
    /// <summary>无权限。在 <see cref="RolePermissions.Has"/> 里被当成"不需要权限"，恒为 true。</summary>
    None = 0,

    /// <summary>看运行页（图像 + 判定 + 指标）</summary>
    ViewRun = 1 << 0,

    /// <summary>启动 / 停止检测</summary>
    StartStop = 1 << 1,

    /// <summary>报警确认</summary>
    AckAlarm = 1 << 2,

    /// <summary>查历史记录</summary>
    ViewHistory = 1 << 3,

    /// <summary>切换配方（换型）</summary>
    SwitchRecipe = 1 << 4,

    /// <summary>看统计报表</summary>
    ViewStats = 1 << 5,

    /// <summary>改参数（阈值、公差这类）</summary>
    EditParameters = 1 << 6,

    /// <summary>
    /// 进工程师模式（图像来源页、日志、控制面板维护区全开）。
    /// 注意它管的是"能不能进入那个界面状态"，不是"进入之后能不能做每一个动作"——
    /// 后者仍然各自有权限位。
    /// </summary>
    EnterEngineerMode = 1 << 7,

    /// <summary>
    /// 改相机设置（曝光、增益）。
    /// <b>已失效</b>：相机层删除后本程序不再有相机参数可调（页面换成了"图像来源"，
    /// 那页由 <see cref="EnterEngineerMode"/> 把关）。这一位仍发给工程师，但没有任何地方查询它。
    /// </summary>
    CameraSettings = 1 << 8,

    /// <summary>编辑配方内容（不只是切换）</summary>
    EditRecipe = 1 << 9,

    /// <summary>用户管理（建人、停用、改密）</summary>
    ManageUsers = 1 << 10,

    /// <summary>查审计流水</summary>
    ViewAudit = 1 << 11,
}

/// <summary>
/// 角色到权限的映射。
///
/// <para>
/// <b>定位</b>：这是"操作员能不能改配方"这个问题的<b>唯一权威答案</b>。
/// 放在 Core 层且写成纯函数，是为了让它可被单测覆盖 ——
/// 而不是只存在于某个按钮的 IsEnabled 绑定里。
/// </para>
///
/// <para>
/// <b>归属</b>：无状态静态类，任何线程可调；没有初始化、没有缓存、没有生命周期。
/// 输入只有角色与权限位，输出是 bool / 字符串 —— 同一个输入永远同一个结果，
/// 所以审计里"当时按的是什么规则"可以事后复现。
/// </para>
///
/// <para>
/// <b>两条边界值得单独强调（都来自车间安全规矩）</b>：
/// <list type="number">
///   <item>用户管理、查审计只给管理员 —— 工程师能改参数，但不能管人，也不能擦自己的脚印；</item>
///   <item>停止与急停不做权限限制 —— 见 <see cref="RequiresPermission"/> 的说明。
///         权限系统如果把"停机"挡住，出事时"我登不上所以停不了"就是设计杀人。</item>
/// </list>
/// </para>
/// </summary>
public static class RolePermissions
{
    /// <summary>操作员：启停、看运行页、报警确认、查历史</summary>
    private const Permission OperatorPermissions =
        Permission.ViewRun | Permission.StartStop | Permission.AckAlarm | Permission.ViewHistory;

    /// <summary>技术员：+ 切配方、看统计</summary>
    private const Permission TechnicianPermissions =
        OperatorPermissions | Permission.SwitchRecipe | Permission.ViewStats;

    /// <summary>工程师：+ 改参数、进工程师模式、相机设置、编辑配方</summary>
    private const Permission EngineerPermissions =
        TechnicianPermissions | Permission.EditParameters | Permission.EnterEngineerMode
        | Permission.CameraSettings | Permission.EditRecipe;

    /// <summary>管理员：+ 用户管理、查审计</summary>
    private const Permission AdminPermissions =
        EngineerPermissions | Permission.ManageUsers | Permission.ViewAudit;

    /// <summary>
    /// 某角色拥有的全部权限位。
    ///
    /// <b>边界</b>：未知值（枚举被反序列化成 4 以上的数）走 <c>_</c> 分支返回
    /// <see cref="Permission.None"/>，不抛也不回落到 Operator —— 默认拒绝。
    /// 这一点是刻意的：数据库里被手改过的角色值不该换来"操作员"的权限。
    /// </summary>
    public static Permission Of(UserRole role) => role switch
    {
        UserRole.Operator => OperatorPermissions,
        UserRole.Technician => TechnicianPermissions,
        UserRole.Engineer => EngineerPermissions,
        UserRole.Admin => AdminPermissions,
        // 未知角色一律当无权限 —— 枚举反序列化出意外值时不放大权限
        _ => Permission.None,
    };

    /// <summary>
    /// 某角色是否拥有某权限。<c>None</c> 视为"不需要权限"，恒为 true。
    ///
    /// <para>
    /// <b>注意 <c>None</c> 这条捷径</b>：它让"不要求权限的动作"能直接用同一个判断，
    /// 但也意味着"调用方传错成 None"会静默放行。传参一侧必须认真写，
    /// 不要把"还没想清楚要哪一位"写成 None。
    /// </para>
    /// <para>
    /// <b>未登录不在这里判</b>：本方法只看角色，未登录的拦截在
    /// <c>SessionService.HasPermission</c>（<c>_user is null</c> 时只放行 None）。
    /// 绕过会话直接调这里，等于把"没登录"当成操作员。
    /// </para>
    /// </summary>
    public static bool Has(UserRole role, Permission permission)
    {
        if (permission == Permission.None) return true;
        var granted = Of(role);
        return (granted & permission) == permission;
    }

    /// <summary>角色的中文名（界面与审计都用它，避免两处各写一套）</summary>
    public static string Describe(UserRole role) => role switch
    {
        UserRole.Operator => "操作员",
        UserRole.Technician => "技术员",
        UserRole.Engineer => "工程师",
        UserRole.Admin => "管理员",
        _ => "未知角色",
    };

    /// <summary>
    /// 这个动作是否需要权限、需要哪一位。
    ///
    /// <para>
    /// 急停不在这张表里 —— 它不需要任何权限。理由是安全逻辑：
    /// 能操作这台设备的人必须先能停它，权限系统要是把急停挡住，
    /// 出事的时候"我登不上所以停不了"就是设计杀人。停止（Stop）同理。
    /// </para>
    ///
    /// <para>
    /// <b>返回值的三种含义</b>：<see cref="Permission.None"/> = 明确不需要权限；
    /// 某个位 = 需要这一位；<c>null</c> = 这张表没覆盖这个动作（如 <see cref="Models.AuditAction.Login"/>），
    /// 调用方应自行决定默认值 —— <b>不要</b>把 null 当成"放行"。
    /// </para>
    ///
    /// <para>
    /// <b>现状（重要）</b>：<b>没有生产调用者</b>，只有 <c>SecurityTests</c> 在钉这张表。
    /// 实际拦人的是各处零散的 <c>SessionService.HasPermission</c> 调用
    /// （顶栏切模式、侧栏导航）。也就是说这张"动作 → 权限"的权威映射目前是文档 + 单测，
    /// 不是运行时守卫。另有两处与注释不符，读代码时要注意：
    /// <list type="bullet">
    ///   <item><see cref="Models.AuditAction.EStopReset"/> 不在表里 → 走 <c>_ =&gt; null</c>，
    ///         而 <see cref="Models.AuditAction"/> 的枚举注释写的是"只有管理员能做"，两者没对上；</item>
    ///   <item><see cref="Models.AuditAction.PasswordChange"/> 明确返回 None（任何人可改自己的密码），
    ///         与"管理员重置"是两回事，别混。</item>
    /// </list>
    /// </para>
    /// </summary>
    public static Permission? RequiresPermission(Models.AuditAction action) => action switch
    {
        Models.AuditAction.Start => Permission.StartStop,
        Models.AuditAction.Stop => Permission.None,
        Models.AuditAction.EStopPressed => Permission.None,
        Models.AuditAction.ModeSwitch => Permission.EnterEngineerMode,
        Models.AuditAction.EngineReload => Permission.EnterEngineerMode,
        Models.AuditAction.RecipeSwitch => Permission.SwitchRecipe,
        Models.AuditAction.ParameterChange => Permission.EditParameters,
        Models.AuditAction.UserCreate => Permission.ManageUsers,
        Models.AuditAction.UserDisable => Permission.ManageUsers,
        Models.AuditAction.PasswordChange => Permission.None,
        _ => null,
    };
}
