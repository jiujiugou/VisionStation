using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionStation.Core.Models;
using VisionStation.Core.Users;
using VisionStation.Service.Audit;
using VisionStation.Service.Security;

namespace VisionStation.App.ViewModels;

/// <summary>角色下拉项。界面只认 <see cref="Value"/>，显示文字与审计同源（<see cref="RolePermissions.Describe"/>）。</summary>
public sealed record UserRoleOption(string Name, UserRole Value)
{
    public override string ToString() => Name;
}

/// <summary>
/// 账号表格的一行。
///
/// 与 <c>AuditRow</c> 同一思路：这是只读投影，行内容永不变化 ——
/// 刷新时整表替换，所以不需要 <see cref="ObservableObject"/>。
/// </summary>
/// <param name="Account">这一行对应的账号快照（操作前的 before/after 也用它）</param>
public sealed record UserManageRow(UserAccount Account)
{
    public string UserName => Account.UserName;

    public string RoleText => Account.RoleText;

    /// <summary>启用 / 停用。两个字的差别就是"能不能登录"，所以单独一列而不是塞进说明里</summary>
    public string StatusText => Account.IsEnabled ? "启用" : "停用";

    public bool IsEnabled => Account.IsEnabled;

    /// <summary>最后登录时间。没有登录过就直说 —— 新号与长期不用的号要能一眼分开</summary>
    public string LastLoginText => Account.LastLoginAt is { } at
        ? at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        : "从未登录";

    public bool MustChangePassword => Account.MustChangePassword;

    public string MustChangeText => Account.MustChangePassword ? "是" : "否";
}

/// <summary>
/// 用户管理页（F-64）：列表 + 新建 + 停用/启用 + 重置密码 + 改角色。
///
/// <para>
/// <b>为什么没有"删除"按钮</b>：仓储刻意不提供物理删除（审计按账号名串历史，
/// 删号会让三个月前的"谁改的参数"变成查无此人）。"删人"在这套系统里的正确含义
/// 就是停用 —— 登录被拒、记录与流水全部保留。<c>UserRepository</c> 的接口注释里
/// 已经写死了这条约定，本页只是照着它做。
/// </para>
///
/// <para>
/// <b>权限</b>：侧边栏隐藏只是"看不见"，不是防线。每个命令执行前都用
/// <see cref="ISessionService.HasPermission"/>(<see cref="Permission.ManageUsers"/>) 复核一次；
/// 被拒时不仅给提示，还记一条 <see cref="AuditResult.Denied"/> 审计 ——
/// "有人在试"比十次成功操作更值得看。
/// </para>
///
/// <para>
/// <b>审计在这一层记，不在服务里记</b>：身份（谁在操作）只有会话知道，
/// 而 <see cref="IUserService"/> 刻意不认识会话（它连"当前用户"都没有）。
/// 服务只回答"改没改成"，这里负责把动作、对象、前后值一起落成流水。
/// 连 <see cref="AuditResult.Failed"/> 也记 —— 只记成功等于把"按了但没成"这段抹掉。
/// </para>
///
/// <para>
/// <b>构造函数零副作用</b>：只存依赖、给下拉框一个默认值，不查库、不订阅。
/// 首次列表由视图 <c>Loaded</c> 触发 <see cref="RefreshCommand"/>（与审计页、历史页同一写法），
/// 页面常驻单例，切页回来不会反复查库。
/// </para>
/// </summary>
public partial class UserManagePageViewModel : ObservableObject
{
    private readonly IUserService _users;
    private readonly ISessionService _session;
    private readonly IAuditService _audit;

    /// <summary>当前列表。整表替换，不增量更新 —— 账号只有个位数，重建比 diff 便宜。</summary>
    public ObservableCollection<UserManageRow> Users { get; } = new();

    /// <summary>角色下拉。顺序按枚举定义（操作员 → 管理员），与权限累加方向一致。</summary>
    public IReadOnlyList<UserRoleOption> Roles { get; } =
        Enum.GetValues<UserRole>()
            .Select(role => new UserRoleOption(RolePermissions.Describe(role), role))
            .ToList();

    /// <summary>页面上写出来的密码规则。值来自服务里的既有常量，界面不另立一份。</summary>
    public string PasswordRuleText => $"密码至少 {UserService.MinPasswordLength} 位";

    /// <summary>列表选中行。所有"对某个账号"的动作都从它取目标。</summary>
    [ObservableProperty]
    private UserManageRow? _selectedUser;

    /// <summary>选中行要改成的新角色。切行时自动同步成该行当前角色。</summary>
    [ObservableProperty]
    private UserRoleOption? _editRole;

    /// <summary>重置密码的输入。成功后被清空（视图据此清掉 PasswordBox）。</summary>
    [ObservableProperty]
    private string _resetPassword = string.Empty;

    /// <summary>新建账号的用户名</summary>
    [ObservableProperty]
    private string _newUserName = string.Empty;

    /// <summary>新建账号的角色。默认操作员 —— 新建账号时"给最小权限"是安全的默认值。</summary>
    [ObservableProperty]
    private UserRoleOption? _newUserRole;

    /// <summary>新建账号的初始密码。成功后被清空（视图据此清掉 PasswordBox）。</summary>
    [ObservableProperty]
    private string _newUserPassword = string.Empty;

    /// <summary>提示行：成功、被拒、失败的说明都写这里，不弹模态框</summary>
    [ObservableProperty]
    private string _statusText = "正在加载…";

    /// <summary>提示行是否为警告语气（红/棕）</summary>
    [ObservableProperty]
    private bool _statusIsWarning;

    /// <summary>是否正在读写账号表（刷新按钮防连点）</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// 构造：只装配依赖与下拉默认值。
    /// </summary>
    public UserManagePageViewModel(IUserService users, ISessionService session, IAuditService audit)
    {
        _users = users;
        _session = session;
        _audit = audit;

        NewUserRole = Roles.FirstOrDefault(r => r.Value == UserRole.Operator) ?? Roles[0];
    }

    /// <summary>选中行变了：同步"改角色"下拉，并刷新依赖选中行的按钮文案。</summary>
    partial void OnSelectedUserChanged(UserManageRow? value)
    {
        EditRole = value is null
            ? null
            : Roles.FirstOrDefault(r => r.Value == value.Account.Role);

        OnPropertyChanged(nameof(ToggleEnabledText));
        OnPropertyChanged(nameof(SelectedUserName));
    }

    /// <summary>选中行的用户名。未选中时给一句明确的话 —— 空白会让人以为是加载失败。</summary>
    public string SelectedUserName => SelectedUser?.UserName ?? "（未选中）";

    /// <summary>停用/启用按钮的文案。跟着选中行的当前状态走，避免"按钮写着停用、点的却是启用"。</summary>
    public string ToggleEnabledText
        => SelectedUser is { IsEnabled: true } ? "停用该账号" : "启用该账号";

    /// <summary>当前操作人。服务用它做"不能停用自己"的判定；取不到就留空（未登录本来就过不了权限门）。</summary>
    private string Actor => _session.CurrentUser?.UserName ?? string.Empty;

    /// <summary>
    /// 刷新列表。
    ///
    /// 查询失败要说出来：静默留一张空表会被读成"系统里没有账号"，
    /// 而那正好是用户管理页最不能出现的结论。
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading) return;

        if (!_session.HasPermission(Permission.ManageUsers))
        {
            Users.Clear();
            SelectedUser = null;
            ShowWarning("没有用户管理权限：只有管理员能查看和修改账号。");
            return;
        }

        if (await LoadAsync(SelectedUser?.UserName))
            ShowInfo(Users.Count == 0 ? "还没有任何账号" : $"共 {Users.Count} 个账号");
    }

    /// <summary>新建账号：用户名 + 角色 + 初始密码，建完强制首次改密。</summary>
    [RelayCommand]
    private async Task CreateAsync()
    {
        var name = (NewUserName ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            ShowWarning("请先填用户名。");
            return;
        }

        if (NewUserRole is not { } role)
        {
            ShowWarning("请选择新账号的角色。");
            return;
        }

        if (!RequirePermission(AuditAction.UserCreate, name)) return;

        var result = await ExecuteAsync(AuditAction.UserCreate, name,
            () => _users.CreateUserAsync(name, role.Value, NewUserPassword ?? string.Empty, Actor));

        if (!result.Ok) return;

        _audit.Record(AuditAction.UserCreate, name,
            after: $"角色={role.Name}；首次登录必须改密",
            detail: "管理员新建账号");

        // 成功后清掉密码：初始密码只该被用一次，留在界面上等于挂在屏幕上
        NewUserName = string.Empty;
        NewUserPassword = string.Empty;

        if (await LoadAsync(name))
            ShowInfo($"已新建账号「{name}」（{role.Name}），首次登录必须修改密码。");
    }

    /// <summary>
    /// 停用 / 启用选中账号。
    ///
    /// 停用之后该账号立即无法登录（<c>AuthOutcome.Disabled</c>），
    /// 但记录与历史审计都还在 —— 这就是本系统里"删人"的全部含义。
    /// </summary>
    [RelayCommand]
    private async Task ToggleEnabledAsync()
    {
        if (SelectedUser is not { } row)
        {
            ShowWarning("请先在列表里选中一个账号。");
            return;
        }

        var enable = !row.IsEnabled;
        var action = enable ? AuditAction.UserEnable : AuditAction.UserDisable;

        if (!RequirePermission(action, row.UserName)) return;

        var result = await ExecuteAsync(action, row.UserName,
            () => _users.SetUserEnabledAsync(row.UserName, enable, Actor));

        if (!result.Ok) return;

        _audit.Record(action, row.UserName,
            before: row.StatusText,
            after: enable ? "启用" : "停用",
            detail: enable ? "管理员启用账号" : "管理员停用账号（该账号已无法登录）");

        if (await LoadAsync(row.UserName))
            ShowInfo(enable
                ? $"已启用「{row.UserName}」。"
                : $"已停用「{row.UserName}」，该账号现在无法登录（记录与审计都保留）。");
    }

    /// <summary>
    /// 重置选中账号的密码。
    ///
    /// 不需要旧密码（管理员不可能知道别人的密码），重置后强制本人下次登录改密 ——
    /// 否则这串"管理员知道的密码"就会一直有效。
    /// <b>审计只记"重置过"，绝不记密码内容。</b>
    /// </summary>
    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        if (SelectedUser is not { } row)
        {
            ShowWarning("请先在列表里选中一个账号。");
            return;
        }

        if (!RequirePermission(AuditAction.PasswordReset, row.UserName)) return;

        var result = await ExecuteAsync(AuditAction.PasswordReset, row.UserName,
            () => _users.ResetPasswordAsync(row.UserName, ResetPassword ?? string.Empty, Actor));

        if (!result.Ok) return;

        _audit.Record(AuditAction.PasswordReset, row.UserName,
            after: "已重置，下次登录必须修改",
            detail: "管理员重置密码（不记录密码内容）");

        ResetPassword = string.Empty;

        if (await LoadAsync(row.UserName))
            ShowInfo($"已重置「{row.UserName}」的密码，其下次登录必须修改。");
    }

    /// <summary>
    /// 改选中账号的角色。
    ///
    /// 角色要重新登录才在该账号的会话里生效（会话持有的是登录那一刻的快照），
    /// 所以成功后提示里必须说清楚 —— 否则现场会以为"改了没生效"。
    /// </summary>
    [RelayCommand]
    private async Task SaveRoleAsync()
    {
        if (SelectedUser is not { } row)
        {
            ShowWarning("请先在列表里选中一个账号。");
            return;
        }

        if (EditRole is not { } role)
        {
            ShowWarning("请选择要改成的角色。");
            return;
        }

        if (role.Value == row.Account.Role)
        {
            ShowInfo("角色没有变化。");
            return;
        }

        if (!RequirePermission(AuditAction.UserRoleChange, row.UserName)) return;

        var result = await ExecuteAsync(AuditAction.UserRoleChange, row.UserName,
            () => _users.ChangeRoleAsync(row.UserName, role.Value, Actor));

        if (!result.Ok) return;

        _audit.Record(AuditAction.UserRoleChange, row.UserName,
            before: row.RoleText,
            after: role.Name,
            detail: "管理员修改账号角色（本人重新登录后生效）");

        if (await LoadAsync(row.UserName))
            ShowInfo($"已把「{row.UserName}」的角色改为 {role.Name}（本人重新登录后生效）。");
    }

    /// <summary>
    /// 权限复核。侧边栏隐藏不算防线 —— 页面可能因为降级、切号、或者有人直接改
    /// <c>CurrentPage</c> 而仍然停在屏幕上。
    ///
    /// <para>
    /// 被拒时记 <see cref="AuditResult.Denied"/>：这是"有人试图越权"的唯一线索，
    /// 比成功记录更值得留。返回 false 表示"不要往下走"。
    /// </para>
    /// </summary>
    private bool RequirePermission(AuditAction action, string target)
    {
        if (_session.HasPermission(Permission.ManageUsers)) return true;

        _audit.Record(action, target, result: AuditResult.Denied,
            detail: "没有用户管理权限（需要管理员）");

        ShowWarning("没有用户管理权限：请用管理员账号登录后再操作。");
        return false;
    }

    /// <summary>
    /// 调一次服务动作，把两类失败都翻译成人话并留痕：
    /// 服务返回的"不允许"（撞名、最后一个管理员…）和仓储抛出的真故障（库被锁、磁盘满）。
    ///
    /// 两条都记 <see cref="AuditResult.Failed"/>：审计三态里 Denied 是"权限挡下"，
    /// Failed 才是"按了但没成"。混用会让审计页分不清越权尝试和操作失败。
    /// </summary>
    private async Task<UserAdminResult> ExecuteAsync(
        AuditAction action, string target, Func<Task<UserAdminResult>> call)
    {
        try
        {
            var result = await call();

            if (!result.Ok)
            {
                _audit.Record(action, target, result: AuditResult.Failed, detail: result.Error);
                ShowWarning(result.Error ?? "操作失败");
            }

            return result;
        }
        catch (Exception ex)
        {
            _audit.Record(action, target, result: AuditResult.Failed, detail: ex.Message);
            ShowWarning($"操作失败：{ex.Message}");
            return UserAdminResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// 重新读一遍账号表。成功返回 true；失败时清空列表并写警告 ——
    /// 不返回半截数据：列表里少一个人比整表清空更容易被误读。
    /// </summary>
    /// <param name="selectUserName">刷新后要重新选中的账号名（操作完保持在原来那一行）</param>
    private async Task<bool> LoadAsync(string? selectUserName)
    {
        IsLoading = true;

        try
        {
            var accounts = await _users.GetUsersAsync();

            Users.Clear();
            foreach (var account in accounts)
                Users.Add(new UserManageRow(account));

            SelectedUser = string.IsNullOrWhiteSpace(selectUserName)
                ? null
                : Users.FirstOrDefault(r =>
                    string.Equals(r.UserName, selectUserName, StringComparison.OrdinalIgnoreCase));

            return true;
        }
        catch (Exception ex)
        {
            Users.Clear();
            SelectedUser = null;
            ShowWarning($"读取账号失败：{ex.Message}");
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>提示行：普通说明（成功、共 N 条）。**不要用它报失败**。</summary>
    private void ShowInfo(string text)
    {
        StatusText = text;
        StatusIsWarning = false;
    }

    /// <summary>提示行：警告语气。被拒、校验失败、库异常都走这里。</summary>
    private void ShowWarning(string text)
    {
        StatusText = text;
        StatusIsWarning = true;
    }
}
