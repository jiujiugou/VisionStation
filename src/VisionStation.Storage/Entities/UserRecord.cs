using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using VisionStation.Core.Users;

namespace VisionStation.Storage.Entities;

/// <summary>
/// 用户账号 — SQLite 持久化实体。
///
/// 主键用 UserName 而不是自增 Id：账号名本身就是唯一标识，
/// 再加一个 Id 只会让"改用户名"变成一次外键迁移。
/// 用户名不允许改名（要换人就停用旧的、建新的），审计里也按名字记，
/// 这样历史记录不会因为改名而断链。
/// </summary>
[Table("Users")]
public class UserRecord
{
    [Key]
    [MaxLength(64)]
    public string UserName { get; set; } = string.Empty;

    [MaxLength(64)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>角色。EF 存成 int —— 角色顺序有意义（累加继承），存字符串反而要另做映射。</summary>
    public UserRole Role { get; set; } = UserRole.Operator;

    [MaxLength(128)]
    public string PasswordHash { get; set; } = string.Empty;

    [MaxLength(64)]
    public string PasswordSalt { get; set; } = string.Empty;

    public int PasswordIterations { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool MustChangePassword { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? LastLoginAt { get; set; }
}
