namespace VisionStation.Core.Users;

/// <summary>
/// 用户账号（纯数据，无 IO）。
///
/// 密码为什么拆成三个字段存（哈希 / 盐 / 迭代轮数）：
///   轮数单独存是为了「将来提高标准时不用让所有人重设密码」——
///   登录时发现库里的轮数低于当前标准，就拿这次输入的明文重新算一遍存回去。
///   如果只存一个合并后的字符串，升级算法时就只能让全员改密。
///
/// 盐必须一人一份、且随机。同一个密码在两个人身上算出的哈希不同，
/// 否则拖库之后"谁和谁用的同一个密码"一眼就能看出来。
/// </summary>
public sealed record UserAccount
{
    /// <summary>登录名（大小写不敏感比较，但按输入原样存）</summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>显示名（顶栏显示用；空则回落到登录名）</summary>
    public string DisplayName { get; init; } = string.Empty;

    public UserRole Role { get; init; } = UserRole.Operator;

    /// <summary>PBKDF2 结果的 Base64</summary>
    public string PasswordHash { get; init; } = string.Empty;

    /// <summary>随机盐的 Base64</summary>
    public string PasswordSalt { get; init; } = string.Empty;

    /// <summary>算出上面这个哈希用的轮数</summary>
    public int PasswordIterations { get; init; }

    /// <summary>停用后仍保留记录（审计要能查到"这个人曾经存在"），但登录会被拒绝</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>是否必须先改密码（种子账号与管理员重置后为 true）</summary>
    public bool MustChangePassword { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public DateTime? LastLoginAt { get; init; }

    /// <summary>界面上显示的名字：优先显示名，没填就用登录名</summary>
    public string EffectiveDisplayName
        => string.IsNullOrWhiteSpace(DisplayName) ? UserName : DisplayName;

    /// <summary>角色中文名</summary>
    public string RoleText => RolePermissions.Describe(Role);

    /// <summary>该账号拥有的权限位</summary>
    public Permission Permissions => RolePermissions.Of(Role);

    /// <summary>是否拥有某权限</summary>
    public bool Has(Permission permission) => RolePermissions.Has(Role, permission);
}
