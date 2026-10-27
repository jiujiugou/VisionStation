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
///
/// <para>
/// <b>这是全库唯一会被 UPDATE 的表</b>（改密、停用、记最后登录），
/// 另外两张（检测记录、审计）都是纯追加。写入口只有 <c>UserRepository</c>，
/// 调用方只有 <c>UserService</c>。
/// </para>
///
/// <para>
/// <b>主键比较是二进制（区分大小写）</b>，而登录查询走 <c>lower()</c> 不区分大小写 ——
/// 两者口径不同。当前只有"建出厂管理员"一条写入路径（名字是常量），不会出问题；
/// 将来做"新增用户"时必须先在入口统一大小写，否则会出现仅大小写不同的两个账号。
/// </para>
/// </summary>
[Table("Users")]
public class UserRecord
{
    /// <summary>登录名，同时是主键。只允许 ASCII 时大小写折叠才符合直觉（SQLite 的 lower() 只处理 ASCII）。</summary>
    [Key]
    [MaxLength(64)]
    public string UserName { get; set; } = string.Empty;

    /// <summary>显示名（界面上的人名）。可以和登录名不同，不做唯一约束。</summary>
    [MaxLength(64)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>角色。EF 存成 int —— 角色顺序有意义（累加继承），存字符串反而要另做映射。</summary>
    public UserRole Role { get; set; } = UserRole.Operator;

    /// <summary>
    /// 口令派生结果：PBKDF2-SHA256 的 32 字节输出，以 Base64 文本存（44 字符）。
    /// 列宽 128 是留余量：换算法可能让结果变长，而改列宽要重建表。
    /// **不要在任何日志或界面里打印这一列**，它和明文只差一次暴力搜索。
    /// </summary>
    [MaxLength(128)]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// 盐：16 字节随机数的 Base64 文本（24 字符），一个账号一份（防彩虹表、防"同密码同哈希"）。
    /// 必须与 <see cref="PasswordHash"/> 成对更新 —— 换哈希不换盐会让验证永远失败。
    /// </summary>
    [MaxLength(64)]
    public string PasswordSalt { get; set; } = string.Empty;

    /// <summary>
    /// 派生轮数（PBKDF2 的迭代次数，当前标准 12 万）。存下来的目的不是"配置"，而是**升级判据**：
    /// 登录成功且这一列低于当前标准时，会用新轮数重算并写回 —— 老账号因此能自动跟上。
    /// 0 表示这条记录从没算过（正常建号就会写，出现 0 说明数据被手工改过）。
    /// </summary>
    public int PasswordIterations { get; set; }

    /// <summary>是否启用。停用而不删除 —— 删了之后审计里的账号名就查无此人。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 下次登录必须改密。出厂管理员建号时置 true，改过之后置 false。
    /// 这是"出厂密码写在源码里"唯一的安全网 —— 首次登录不改，密码就等于公开的。
    /// </summary>
    public bool MustChangePassword { get; set; }

    /// <summary>建号时间（本地时间）。改密/停用都不会动它 —— 它回答的是"这个人什么时候进的系统"。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 最后一次登录成功的时间（本地时间）。**可空 = 从来没登录过**，
    /// 不是"很久以前"—— 建号但没启用的账号就停在这个状态。
    /// </summary>
    public DateTime? LastLoginAt { get; set; }
}
