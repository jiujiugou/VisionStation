using VisionStation.App.ViewModels;
using VisionStation.Core.Models;
using VisionStation.Core.Users;
using VisionStation.Service.Security;

namespace VisionStation.App.Tests;

/// <summary>
/// 用户管理页的界面契约（F-64）：权限复核、审计落点、失败提示。
///
/// <para>
/// 只测"页面决定的事"：有没有先问权限、成功/失败有没有留下流水、给操作员看的是不是人话。
/// 撞名、最后一个管理员这类规则在 <c>UserAdminServiceTests</c> 里测 ——
/// 两边各测各的，页面测试里的服务替身只负责按剧本返回结果。
/// </para>
/// </summary>
public class UserManagePageViewModelTests
{
    /// <summary>
    /// 用户管理服务的替身。
    ///
    /// 记下每次调用的参数（尤其是 <c>actor</c> —— 服务靠它判"不能停用自己"），
    /// 成功时同步改一份 <see cref="Users"/>，让页面刷新后能看到变化。
    /// </summary>
    private sealed class FakeUserService : IUserService
    {
        public List<UserAccount> Users { get; } = new();

        public int GetUsersCalls { get; private set; }

        public UserAdminResult CreateResult { get; set; } = UserAdminResult.Success;
        public UserAdminResult SetEnabledResult { get; set; } = UserAdminResult.Success;
        public UserAdminResult ResetResult { get; set; } = UserAdminResult.Success;
        public UserAdminResult RoleResult { get; set; } = UserAdminResult.Success;

        public List<(string Name, UserRole Role, string Password, string Actor)> CreateCalls { get; } = new();
        public List<(string Name, bool Enabled, string Actor)> SetEnabledCalls { get; } = new();
        public List<(string Name, string Password, string Actor)> ResetCalls { get; } = new();
        public List<(string Name, UserRole Role, string Actor)> RoleCalls { get; } = new();

        public Task<AuthResult> AuthenticateAsync(string userName, string password, CancellationToken ct = default)
            => Task.FromResult(new AuthResult(AuthOutcome.UserNotFound, null));

        public Task<(bool Ok, string? Error)> ChangePasswordAsync(
            string userName, string currentPassword, string newPassword, CancellationToken ct = default)
            => Task.FromResult<(bool Ok, string? Error)>((false, "测试替身不支持本人改密"));

        public Task EnsureDefaultAdminAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<UserAccount>> GetUsersAsync(CancellationToken ct = default)
        {
            GetUsersCalls++;
            return Task.FromResult<IReadOnlyList<UserAccount>>(Users.ToList());
        }

        public Task<UserAdminResult> CreateUserAsync(
            string userName, UserRole role, string initialPassword, string actor, CancellationToken ct = default)
        {
            CreateCalls.Add((userName, role, initialPassword, actor));

            if (CreateResult.Ok)
            {
                Users.Add(new UserAccount
                {
                    UserName = userName,
                    DisplayName = userName,
                    Role = role,
                    IsEnabled = true,
                    MustChangePassword = true,
                });
            }

            return Task.FromResult(CreateResult);
        }

        public Task<UserAdminResult> SetUserEnabledAsync(
            string userName, bool enabled, string actor, CancellationToken ct = default)
        {
            SetEnabledCalls.Add((userName, enabled, actor));

            if (SetEnabledResult.Ok)
                Replace(userName, u => u with { IsEnabled = enabled });

            return Task.FromResult(SetEnabledResult);
        }

        public Task<UserAdminResult> ResetPasswordAsync(
            string userName, string newPassword, string actor, CancellationToken ct = default)
        {
            ResetCalls.Add((userName, newPassword, actor));

            if (ResetResult.Ok)
                Replace(userName, u => u with { MustChangePassword = true });

            return Task.FromResult(ResetResult);
        }

        public Task<UserAdminResult> ChangeRoleAsync(
            string userName, UserRole role, string actor, CancellationToken ct = default)
        {
            RoleCalls.Add((userName, role, actor));

            if (RoleResult.Ok)
                Replace(userName, u => u with { Role = role });

            return Task.FromResult(RoleResult);
        }

        private void Replace(string userName, Func<UserAccount, UserAccount> update)
        {
            var index = Users.FindIndex(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) Users[index] = update(Users[index]);
        }
    }

    private static UserAccount Account(
        string name, UserRole role = UserRole.Operator, bool enabled = true, bool mustChange = false)
        => new()
        {
            UserName = name,
            DisplayName = name,
            Role = role,
            IsEnabled = enabled,
            MustChangePassword = mustChange,
        };

    /// <summary>造一个页面。会话角色决定权限门，actor 决定服务里"不能停用自己"的判定对象。</summary>
    private static UserManagePageViewModel NewPage(
        FakeUserService users, FakeAuditService audit, UserRole role = UserRole.Admin, string actor = "admin")
    {
        var session = new FakeSessionService();
        session.SignIn(role, actor);
        return new UserManagePageViewModel(users, session, audit);
    }

    // ── 权限 ──

    /// <summary>工程师看不了这张表：侧边栏隐藏不算防线，查询本身也要复核。</summary>
    [Fact]
    public async Task Refresh_WithoutManageUsersPermission_DoesNotQuery()
    {
        var users = new FakeUserService();
        var audit = new FakeAuditService();
        var vm = NewPage(users, audit, UserRole.Engineer, "eng");

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(0, users.GetUsersCalls);
        Assert.Empty(vm.Users);
        Assert.True(vm.StatusIsWarning);
        Assert.Contains("权限", vm.StatusText);
    }

    /// <summary>
    /// 无权限时不能往下走到服务，而且要留一条 Denied 流水 ——
    /// "有人在试"比十次成功操作更值得看。
    /// </summary>
    [Fact]
    public async Task Create_WithoutManageUsersPermission_IsDeniedAndAudited()
    {
        var users = new FakeUserService();
        var audit = new FakeAuditService();
        var vm = NewPage(users, audit, UserRole.Engineer, "eng");
        vm.NewUserName = "alice";
        vm.NewUserPassword = "1234";

        await vm.CreateCommand.ExecuteAsync(null);

        Assert.Empty(users.CreateCalls);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditAction.UserCreate, entry.Action);
        Assert.Equal(AuditResult.Denied, entry.Result);
        Assert.True(vm.StatusIsWarning);
        Assert.Contains("权限", vm.StatusText);
    }

    // ── 新建 ──

    /// <summary>
    /// 成功路径：服务收到的 actor 是当前登录人，审计记成功 + 具体角色，
    /// 表单清空，列表刷新——四件事缺一条，现场就分不清"到底建没建成"。
    /// </summary>
    [Fact]
    public async Task Create_Success_WritesAuditAndClearsForm()
    {
        var users = new FakeUserService();
        var audit = new FakeAuditService();
        var vm = NewPage(users, audit);

        vm.NewUserName = "  alice  ";
        vm.NewUserRole = vm.Roles.First(r => r.Value == UserRole.Technician);
        vm.NewUserPassword = "1234";

        await vm.CreateCommand.ExecuteAsync(null);

        var call = Assert.Single(users.CreateCalls);
        Assert.Equal("alice", call.Name);              // 前后空格已去掉
        Assert.Equal(UserRole.Technician, call.Role);
        Assert.Equal("1234", call.Password);
        Assert.Equal("admin", call.Actor);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditAction.UserCreate, entry.Action);
        Assert.Equal("alice", entry.Target);
        Assert.Equal(AuditResult.Success, entry.Result);
        Assert.Contains("技术员", entry.After!);

        Assert.Equal(string.Empty, vm.NewUserName);
        Assert.Equal(string.Empty, vm.NewUserPassword);
        Assert.Contains("alice", vm.StatusText);
        Assert.Contains("首次登录", vm.StatusText);
        Assert.False(vm.StatusIsWarning);

        Assert.Equal("alice", Assert.Single(vm.Users).UserName);
    }

    /// <summary>
    /// 服务拒绝（撞名、密码太短…）时：原话照搬给人看，记一条 Failed，
    /// 并且<b>不清空输入</b>—— 操作员多半只需要改一个字。
    /// </summary>
    [Fact]
    public async Task Create_WhenServiceRejects_ShowsHumanMessageAndRecordsFailed()
    {
        var users = new FakeUserService
        {
            CreateResult = UserAdminResult.Fail("账号「alice」已存在（用户名不区分大小写）"),
        };
        var audit = new FakeAuditService();
        var vm = NewPage(users, audit);
        vm.NewUserName = "alice";
        vm.NewUserPassword = "1234";

        await vm.CreateCommand.ExecuteAsync(null);

        Assert.Equal("账号「alice」已存在（用户名不区分大小写）", vm.StatusText);
        Assert.True(vm.StatusIsWarning);
        Assert.Equal("alice", vm.NewUserName);   // 输入保留

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditResult.Failed, entry.Result);
        Assert.Contains("已存在", entry.Detail!);
    }

    // ── 停用 / 启用 ──

    /// <summary>没选中行时先给提示，不要把"空目标"发到服务层去撞一次。</summary>
    [Fact]
    public async Task ToggleEnabled_WithoutSelection_ShowsWarning()
    {
        var users = new FakeUserService();
        var audit = new FakeAuditService();
        var vm = NewPage(users, audit);

        await vm.ToggleEnabledCommand.ExecuteAsync(null);

        Assert.Empty(users.SetEnabledCalls);
        Assert.True(vm.StatusIsWarning);
        Assert.Contains("选中", vm.StatusText);
    }

    /// <summary>
    /// 停用走 UserDisable、启用走 UserEnable（两个动作分开），before/after 是状态文字。
    /// 这一条同时钉住"按钮文案跟着选中行走"—— 界面上写"停用"就必须真的发 false。
    /// </summary>
    [Fact]
    public async Task ToggleEnabled_DisableThenEnable_RecordsBothDirections()
    {
        var users = new FakeUserService();
        users.Users.Add(Account("bob", mustChange: true));
        var audit = new FakeAuditService();
        var vm = NewPage(users, audit);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedUser = vm.Users[0];
        Assert.Equal("停用该账号", vm.ToggleEnabledText);

        await vm.ToggleEnabledCommand.ExecuteAsync(null);

        var call = Assert.Single(users.SetEnabledCalls);
        Assert.Equal("bob", call.Name);
        Assert.False(call.Enabled);
        Assert.Equal("admin", call.Actor);

        var disable = Assert.Single(audit.Entries);
        Assert.Equal(AuditAction.UserDisable, disable.Action);
        Assert.Equal("启用", disable.Before);
        Assert.Equal("停用", disable.After);
        Assert.Contains("无法登录", disable.Detail!);

        // 刷新后仍选中同一个人，按钮应翻成"启用"
        vm.SelectedUser = vm.Users[0];
        Assert.False(vm.SelectedUser!.IsEnabled);
        Assert.Equal("启用该账号", vm.ToggleEnabledText);

        await vm.ToggleEnabledCommand.ExecuteAsync(null);

        var enable = audit.Entries[^1];
        Assert.Equal(AuditAction.UserEnable, enable.Action);
        Assert.Equal("停用", enable.Before);
        Assert.Equal("启用", enable.After);
    }

    /// <summary>最后一个管理员被服务拦下时，页面要把原因原样说出来并记 Failed。</summary>
    [Fact]
    public async Task ToggleEnabled_WhenServiceRejects_ShowsMessageAndRecordsFailed()
    {
        var users = new FakeUserService
        {
            SetEnabledResult = UserAdminResult.Fail("这是最后一个启用中的管理员，停用后就没人能管理账号了"),
        };
        users.Users.Add(Account("admin", UserRole.Admin));
        var audit = new FakeAuditService();
        var vm = NewPage(users, audit);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedUser = vm.Users[0];

        await vm.ToggleEnabledCommand.ExecuteAsync(null);

        Assert.Equal("这是最后一个启用中的管理员，停用后就没人能管理账号了", vm.StatusText);
        Assert.True(vm.StatusIsWarning);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditAction.UserDisable, entry.Action);
        Assert.Equal(AuditResult.Failed, entry.Result);
    }

    // ── 重置密码 ──

    /// <summary>
    /// 重置成功要记 PasswordReset（不是 PasswordChange：那是本人凭旧密码改的），
    /// 而且<b>审计里绝不能出现密码内容</b>。
    /// </summary>
    [Fact]
    public async Task ResetPassword_Success_WritesAuditWithoutPassword()
    {
        var users = new FakeUserService();
        users.Users.Add(Account("bob", mustChange: false));
        var audit = new FakeAuditService();
        var vm = NewPage(users, audit);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedUser = vm.Users[0];
        vm.ResetPassword = "secret99";

        await vm.ResetPasswordCommand.ExecuteAsync(null);

        var call = Assert.Single(users.ResetCalls);
        Assert.Equal("bob", call.Name);
        Assert.Equal("secret99", call.Password);
        Assert.Equal("admin", call.Actor);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditAction.PasswordReset, entry.Action);
        Assert.Equal("bob", entry.Target);
        Assert.Null(entry.Before);
        Assert.Contains("必须修改", entry.After!);
        Assert.DoesNotContain("secret99", entry.After!);
        Assert.DoesNotContain("secret99", entry.Detail!);

        Assert.Equal(string.Empty, vm.ResetPassword);
        Assert.True(vm.Users[0].MustChangePassword);
    }

    // ── 改角色 ──

    /// <summary>改角色要记前后两个角色名：只记"改过角色"在追责时等于没记。</summary>
    [Fact]
    public async Task SaveRole_Success_RecordsBeforeAndAfter()
    {
        var users = new FakeUserService();
        users.Users.Add(Account("bob", UserRole.Operator));
        var audit = new FakeAuditService();
        var vm = NewPage(users, audit);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedUser = vm.Users[0];

        // 切行时下拉应同步成该行当前角色
        Assert.Equal(UserRole.Operator, vm.EditRole!.Value);

        vm.EditRole = vm.Roles.First(r => r.Value == UserRole.Engineer);
        await vm.SaveRoleCommand.ExecuteAsync(null);

        var call = Assert.Single(users.RoleCalls);
        Assert.Equal(UserRole.Engineer, call.Role);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditAction.UserRoleChange, entry.Action);
        Assert.Equal("操作员", entry.Before);
        Assert.Equal("工程师", entry.After);
        Assert.Equal(UserRole.Engineer, vm.Users[0].Account.Role);
    }
}
