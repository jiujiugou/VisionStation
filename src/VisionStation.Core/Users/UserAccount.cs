namespace VisionStation.Core.Users;

/// <summary>
/// 用户账号（纯数据，无 IO）。
///
/// <para>
/// <b>定位</b>：登录与权限判断的输入。上游是 <c>UserRepository</c>（表 → 对象）与
/// <c>UserService</c>（建号 / 改密 / 停用时组装），下游是 <c>SessionService</c>
/// （登录成功后一直持有它，作为 <c>CurrentUser</c>）与顶栏（显示名 + 角色）。
/// </para>
///
/// <para>
/// <b>归属</b>：<b>不可变</b>（record + 全部 <c>init</c>）。改密码/停用不是就地改字段，
/// 而是生成一个新对象再落库；会话里持有的那份也不会被别处的更新"顺手改掉"。
/// 这让"登录后权限被悄悄改了"这种问题不可能发生 —— 要变就换人重登。
/// </para>
///
/// <para>
/// <b>失败行为</b>：纯数据，不抛异常、不做校验。<b>它从不自己判断密码对不对</b> ——
/// 校验在 <c>UserService</c>（PBKDF2 + 失败计数 + 锁定），这里只存结果。
/// 把校验写进实体是常见错误：那样"验证过了"会变成对象的状态，容易被复用。
/// </para>
///
/// <para>
/// 密码为什么拆成三个字段存（哈希 / 盐 / 迭代轮数）：
///   轮数单独存是为了「将来提高标准时不用让所有人重设密码」——
///   登录时发现库里的轮数低于当前标准，就拿这次输入的明文重新算一遍存回去
///   （<c>UserService</c> 里的 <c>NeedsUpgrade</c> 分支）。如果只存一个合并后的字符串，
///   升级算法时就只能让全员改密，而现场没人愿意在换型间隙改密码。
/// </para>
///
/// <para>
/// 盐必须一人一份、且随机。同一个密码在两个人身上算出的哈希不同，
/// 否则拖库之后"谁和谁用的同一个密码"一眼就能看出来。
/// </para>
/// </summary>
public sealed record UserAccount
{
    /// <summary>
    /// 登录名（大小写不敏感比较，但按输入原样存）。
    /// 它是审计里的"人"的唯一标识，所以改名等于换人 —— 历史记录不会跟着变。
    /// </summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>显示名（顶栏显示用；空则回落到登录名）</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// 账号的固有角色。<b>不要直接用它判权限</b> —— 会话超时会降级，
    /// 判权重要走 <c>SessionService.EffectiveRole</c> / <c>HasPermission</c>，
    /// 否则降级形同虚设（人离开工位 5 分钟后仍是工程师）。
    /// </summary>
    public UserRole Role { get; init; } = UserRole.Operator;

    /// <summary>PBKDF2 结果的 Base64。不要在任何日志/审计里输出这个字段。</summary>
    public string PasswordHash { get; init; } = string.Empty;

    /// <summary>随机盐的 Base64（一人一份）</summary>
    public string PasswordSalt { get; init; } = string.Empty;

    /// <summary>
    /// 算出上面这个哈希用的轮数。登录成功时若发现它低于当前标准，
    /// 会用刚输入的明文重算并写回 —— 这就是"不用全员改密也能升算法"的落点。
    /// </summary>
    public int PasswordIterations { get; init; }

    /// <summary>
    /// 停用后仍保留记录（审计要能查到"这个人曾经存在"），但登录会被拒绝。
    /// 不做物理删除是有意的：删掉账号会让历史审计里的名字变成查无此人。
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// 是否必须先改密码（种子账号与管理员重置后为 true）。
    /// 它只是一个标记：真正的强制发生在登录流程里 —— 改密前不放行任何操作。
    /// </summary>
    public bool MustChangePassword { get; init; }

    /// <summary>建号时间（本机时间）。只用于展示与排查，不参与任何过期判断。</summary>
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    /// <summary>
    /// 最近一次登录成功的时间。首次登录前为 null（不是 <c>DateTime.MinValue</c>）——
    /// "从来没登录过"和"很久以前登录过"是两件事，新号要能一眼看出来。
    /// </summary>
    public DateTime? LastLoginAt { get; init; }

    /// <summary>界面上显示的名字：优先显示名，没填就用登录名（避免顶栏出现空白）</summary>
    public string EffectiveDisplayName
        => string.IsNullOrWhiteSpace(DisplayName) ? UserName : DisplayName;

    /// <summary>角色中文名（与审计里的写法同源，见 <see cref="RolePermissions.Describe"/>）</summary>
    public string RoleText => RolePermissions.Describe(Role);

    /// <summary>
    /// 该账号<b>固有角色</b>拥有的权限位。
    /// 判动作请用 <c>SessionService.HasPermission</c>（看的是降级后的有效角色），
    /// 理由见 <see cref="Role"/>。
    /// </summary>
    public Permission Permissions => RolePermissions.Of(Role);

    /// <summary>是否拥有某权限（同上：看的是固有角色，不含超时降级）</summary>
    public bool Has(Permission permission) => RolePermissions.Has(Role, permission);
}
