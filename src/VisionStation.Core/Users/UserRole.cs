namespace VisionStation.Core.Users;

/// <summary>
/// 用户角色。四级，权限累加继承。
///
/// 为什么是四级而不是"管理员/普通用户"两级：
///   产线上这三类人干的活不一样，混成两级就必然出现"操作员也能改配方"。
///   四级对应四件不同的事 —— 开机停机（操作员）、换型号（技术员）、
///   调参数和进相机设置（工程师）、管人和查账（管理员）。
///
/// 出处：注塑机 HMI 改造的十条原则给出的正是这套分组
/// （操作员 / 班组长 / 设备工程师 / 管理员），本项目的 Technician 对应"班组长"。
/// </summary>
public enum UserRole
{
    /// <summary>操作员：启停、看状态、报警确认、查历史</summary>
    Operator = 0,

    /// <summary>技术员：操作员的全部 + 切配方、看统计</summary>
    Technician = 1,

    /// <summary>工程师：技术员的全部 + 改参数、进工程师模式、相机设置、编辑配方</summary>
    Engineer = 2,

    /// <summary>管理员：全部 + 用户管理、审计查询</summary>
    Admin = 3,
}

/// <summary>
/// 权限位。用 <c>[Flags]</c> 是为了"角色 = 若干权限的并集"这个语义能被累加表达。
///
/// 只列本项目真会拦的动作。没有"复位急停"这一位 —— 当前程序里急停只是停触发源，
/// 没有"复位"这个动作，为一个不存在的按钮加权限位，等于给自己留一个永远为真的判断。
/// </summary>
[Flags]
public enum Permission
{
    /// <summary>无权限</summary>
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

    /// <summary>进工程师模式（相机页、日志、控制面板全开）</summary>
    EnterEngineerMode = 1 << 7,

    /// <summary>改相机设置（曝光、增益）</summary>
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
/// 放在 Core 层且写成纯函数，是为了让它可被单测覆盖 ——
/// "操作员能不能改配方"这个问题的答案，不该只存在于某个按钮的 IsEnabled 绑定里。
///
/// 两条边界值得单独强调（都来自车间安全规矩）：
///   1) 用户管理、查审计只给管理员 —— 工程师能改参数，但不能管人，也不能擦自己的脚印；
///   2) 急停不做权限限制 —— 见 <see cref="RequiresPermission"/> 的说明。
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

    /// <summary>某角色拥有的全部权限位</summary>
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
    /// 急停不在这张表里 —— 它不需要任何权限。理由是安全逻辑：
    /// 能操作这台设备的人必须先能停它，权限系统要是把急停挡住，
    /// 出事的时候"我登不上所以停不了"就是设计杀人。停止（Stop）同理。
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
