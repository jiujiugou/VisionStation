using VisionStation.Core.Models;
using VisionStation.Core.Users;
using VisionStation.Service.Audit;
using VisionStation.Service.Security;
using VisionStation.Storage;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// 用户管理的服务层规则（F-64）：新建 / 停用启用 / 重置密码 / 改角色。
///
/// <para>
/// 全部跑在真实 SQLite 临时库上 —— 这些规则里有一半依赖"库里现在还有几个启用中的管理员"，
/// 用内存替身会把真正要验的那次查询换掉，测出来的只是替身自己的算术。
/// </para>
///
/// <para>
/// 这里刻意<b>不测审计</b>：管理动作的流水由页面 VM 记（身份只有会话知道），
/// 服务层只回答"改没改成"。审计的断言在 <c>UserManagePageViewModelTests</c>。
/// </para>
/// </summary>
public class UserAdminServiceTests
{
    /// <summary>服务层的账号操作不记审计；认证路径会记，但本类不关心内容。</summary>
    private sealed class SilentAudit : IAuditService
    {
        public void Record(AuditAction action, string target = "", string? before = null,
            string? after = null, AuditResult result = AuditResult.Success, string? detail = null)
        {
        }

        public void RecordAs(string userName, string userRole, AuditAction action, string target = "",
            string? before = null, string? after = null,
            AuditResult result = AuditResult.Success, string? detail = null)
        {
        }

        public Task<IReadOnlyList<AuditEntry>> QueryRecentAsync(int count = 200)
            => Task.FromResult<IReadOnlyList<AuditEntry>>(Array.Empty<AuditEntry>());

        public Task<IReadOnlyList<AuditEntry>> QueryAsync(
            AuditAction? action = null, string? userName = null, DateTime? since = null, int count = 200)
            => Task.FromResult<IReadOnlyList<AuditEntry>>(Array.Empty<AuditEntry>());

        public void Flush(TimeSpan timeout)
        {
        }
    }

    private sealed class Fixture
    {
        public required UserService Service { get; init; }
        public required UserRepository Repo { get; init; }
    }

    /// <summary>每个用例一份全新的临时库，互不干扰。</summary>
    private static async Task<Fixture> NewFixtureAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs_useradmin_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var repo = new UserRepository(Path.Combine(dir, "visionstation.db"));
        await repo.EnsureCreatedAsync();

        return new Fixture { Service = new UserService(repo, new SilentAudit()), Repo = repo };
    }

    // ── 新建 ──

    /// <summary>新建的号必须强制改密：初始密码是管理员口头/纸面给出去的，可能不止一个人知道。</summary>
    [Fact]
    public async Task CreateUser_NewAccount_ForcesPasswordChange()
    {
        var fixture = await NewFixtureAsync();

        var result = await fixture.Service.CreateUserAsync("alice", UserRole.Technician, "1234", "admin");

        Assert.True(result.Ok);
        Assert.Null(result.Error);

        var user = await fixture.Repo.FindAsync("alice");
        Assert.NotNull(user);
        Assert.Equal(UserRole.Technician, user!.Role);
        Assert.True(user.IsEnabled);
        Assert.True(user.MustChangePassword, "新建账号必须强制首次改密");
        Assert.Null(user.LastLoginAt);
    }

    /// <summary>
    /// 撞名要给人话，不能是主键约束异常。
    /// 大小写不同也算撞名 —— 登录本来就是不区分大小写的，两个号会互相顶替。
    /// </summary>
    [Fact]
    public async Task CreateUser_DuplicateNameIgnoringCase_ReturnsFriendlyError()
    {
        var fixture = await NewFixtureAsync();
        await fixture.Service.CreateUserAsync("Alice", UserRole.Operator, "1234", "admin");

        var result = await fixture.Service.CreateUserAsync("alice", UserRole.Operator, "1234", "admin");

        Assert.False(result.Ok);
        Assert.Contains("已存在", result.Error!);
        // 提示里要带上库里那个已有的写法，操作员才知道该去找谁
        Assert.Contains("Alice", result.Error!);

        // 确实没有建出第二个号
        Assert.Single(await fixture.Repo.GetAllAsync());
    }

    /// <summary>密码策略沿用服务里既有的 MinPasswordLength，不在这里另立一套。</summary>
    [Fact]
    public async Task CreateUser_ShortPassword_IsRejectedWithPolicyText()
    {
        var fixture = await NewFixtureAsync();

        var result = await fixture.Service.CreateUserAsync(
            "bob", UserRole.Operator, new string('1', UserService.MinPasswordLength - 1), "admin");

        Assert.False(result.Ok);
        Assert.Contains(
            UserService.MinPasswordLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            result.Error!);
        Assert.Null(await fixture.Repo.FindAsync("bob"));
    }

    /// <summary>未知角色（枚举越界）不得建号：默认拒绝比默认给操作员权限安全。</summary>
    [Fact]
    public async Task CreateUser_UndefinedRole_IsRejected()
    {
        var fixture = await NewFixtureAsync();

        var result = await fixture.Service.CreateUserAsync("bob", (UserRole)99, "1234", "admin");

        Assert.False(result.Ok);
        Assert.Null(await fixture.Repo.FindAsync("bob"));
    }

    // ── 停用 / 启用 ──

    /// <summary>停用后认证必须返回 Disabled（而不是"密码错"），重新启用后又能登进来。</summary>
    [Fact]
    public async Task DisableUser_ThenAuthenticate_ReturnsDisabled_AndEnableRestoresLogin()
    {
        var fixture = await NewFixtureAsync();
        await fixture.Service.CreateUserAsync("bob", UserRole.Operator, "pass99", "admin");

        var disable = await fixture.Service.SetUserEnabledAsync("bob", false, "admin");
        Assert.True(disable.Ok);

        var blocked = await fixture.Service.AuthenticateAsync("bob", "pass99");
        Assert.Equal(AuthOutcome.Disabled, blocked.Outcome);
        Assert.Null(blocked.User);

        var enable = await fixture.Service.SetUserEnabledAsync("bob", true, "admin");
        Assert.True(enable.Ok);

        var allowed = await fixture.Service.AuthenticateAsync("bob", "pass99");
        Assert.Equal(AuthOutcome.Success, allowed.Outcome);
    }

    /// <summary>不能停用当前登录的账号 —— 手一滑就把自己锁在门外。</summary>
    [Fact]
    public async Task DisableUser_Self_IsRejected()
    {
        var fixture = await NewFixtureAsync();
        await fixture.Service.EnsureDefaultAdminAsync();

        // actor 故意用不同大小写：会话里的名字与库里的写法不一定一致
        var result = await fixture.Service.SetUserEnabledAsync(
            UserService.DefaultAdminUserName, false, "ADMIN");

        Assert.False(result.Ok);
        Assert.Contains("当前登录", result.Error!);

        var admin = await fixture.Repo.FindAsync(UserService.DefaultAdminUserName);
        Assert.True(admin!.IsEnabled);
    }

    /// <summary>最后一个启用中的管理员不能停 —— 停了就再没人能进用户管理页。</summary>
    [Fact]
    public async Task DisableUser_LastEnabledAdmin_IsRejected()
    {
        var fixture = await NewFixtureAsync();
        await fixture.Service.EnsureDefaultAdminAsync();

        // actor 是另一个人：这一条要拦的是"最后一个管理员"，不是"停用自己"
        var result = await fixture.Service.SetUserEnabledAsync(
            UserService.DefaultAdminUserName, false, "someone");

        Assert.False(result.Ok);
        Assert.Contains("最后一个", result.Error!);

        var admin = await fixture.Repo.FindAsync(UserService.DefaultAdminUserName);
        Assert.True(admin!.IsEnabled);
    }

    /// <summary>还有别的管理员在时，停用其中一个必须放行 —— 否则这条保护就变成了"谁也不能动"。</summary>
    [Fact]
    public async Task DisableUser_WhenAnotherEnabledAdminRemains_IsAllowed()
    {
        var fixture = await NewFixtureAsync();
        await fixture.Service.EnsureDefaultAdminAsync();
        await fixture.Service.CreateUserAsync("admin2", UserRole.Admin, "1234", "admin");

        var result = await fixture.Service.SetUserEnabledAsync("admin2", false, "admin");

        Assert.True(result.Ok);
        var admin2 = await fixture.Repo.FindAsync("admin2");
        Assert.False(admin2!.IsEnabled);
    }

    /// <summary>目标已经是该状态时视为成功：界面上连点两下不该得到一句"操作失败"。</summary>
    [Fact]
    public async Task SetEnabled_AlreadyInTargetState_IsIdempotent()
    {
        var fixture = await NewFixtureAsync();
        await fixture.Service.EnsureDefaultAdminAsync();

        var result = await fixture.Service.SetUserEnabledAsync(
            UserService.DefaultAdminUserName, true, "someone");

        Assert.True(result.Ok);
    }

    // ── 重置密码 ──

    /// <summary>
    /// 管理员重置后必须重新置上"下次登录改密"，并且新密码立刻可用、旧密码失效。
    /// 这是"重置"与"本人改密"最关键的区别。
    /// </summary>
    [Fact]
    public async Task ResetPassword_ForcesChangeOnNextLogin()
    {
        var fixture = await NewFixtureAsync();
        await fixture.Service.CreateUserAsync("bob", UserRole.Operator, "oldpass", "admin");

        // 本人先改一次：ChangePasswordAsync 会把改密标志清掉（既有行为）
        var changed = await fixture.Service.ChangePasswordAsync("bob", "oldpass", "ownpass");
        Assert.True(changed.Ok);
        Assert.False((await fixture.Repo.FindAsync("bob"))!.MustChangePassword);

        var reset = await fixture.Service.ResetPasswordAsync("bob", "reset99", "admin");
        Assert.True(reset.Ok);

        var user = await fixture.Repo.FindAsync("bob");
        Assert.NotNull(user);
        Assert.True(user!.MustChangePassword, "重置出来的密码只是临时通行证");

        Assert.Equal(AuthOutcome.Success, (await fixture.Service.AuthenticateAsync("bob", "reset99")).Outcome);
        Assert.Equal(AuthOutcome.WrongPassword, (await fixture.Service.AuthenticateAsync("bob", "ownpass")).Outcome);
    }

    /// <summary>重置也走同一套长度策略（不另立一套）。</summary>
    [Fact]
    public async Task ResetPassword_ShortPassword_IsRejected()
    {
        var fixture = await NewFixtureAsync();
        await fixture.Service.CreateUserAsync("bob", UserRole.Operator, "oldpass", "admin");

        var result = await fixture.Service.ResetPasswordAsync(
            "bob", new string('1', UserService.MinPasswordLength - 1), "admin");

        Assert.False(result.Ok);
        // 旧密码仍然可用 —— 被拒的重置不能把账号弄成半坏状态
        Assert.Equal(AuthOutcome.Success, (await fixture.Service.AuthenticateAsync("bob", "oldpass")).Outcome);
    }

    /// <summary>不存在的账号返回人话，而不是抛仓储异常。</summary>
    [Fact]
    public async Task ResetPassword_UnknownUser_ReturnsFriendlyError()
    {
        var fixture = await NewFixtureAsync();

        var result = await fixture.Service.ResetPasswordAsync("nobody", "1234", "admin");

        Assert.False(result.Ok);
        Assert.Contains("不存在", result.Error!);
    }

    // ── 改角色 ──

    [Fact]
    public async Task ChangeRole_UpdatesRole()
    {
        var fixture = await NewFixtureAsync();
        await fixture.Service.CreateUserAsync("bob", UserRole.Operator, "1234", "admin");

        var result = await fixture.Service.ChangeRoleAsync("bob", UserRole.Engineer, "admin");

        Assert.True(result.Ok);
        var bob = await fixture.Repo.FindAsync("bob");
        Assert.Equal(UserRole.Engineer, bob!.Role);
    }

    /// <summary>
    /// 把最后一个启用中的管理员改成别的角色，后果与停用他完全一样 —— 系统锁死。
    /// 拦的是后果，不是动作名字。
    /// </summary>
    [Fact]
    public async Task ChangeRole_DemotingLastEnabledAdmin_IsRejected()
    {
        var fixture = await NewFixtureAsync();
        await fixture.Service.EnsureDefaultAdminAsync();

        var result = await fixture.Service.ChangeRoleAsync(
            UserService.DefaultAdminUserName, UserRole.Engineer, "someone");

        Assert.False(result.Ok);
        Assert.Contains("最后一个", result.Error!);

        var admin = await fixture.Repo.FindAsync(UserService.DefaultAdminUserName);
        Assert.Equal(UserRole.Admin, admin!.Role);
    }

    /// <summary>角色没变时不写库、不报错（界面上的"保存角色"经常被连点）。</summary>
    [Fact]
    public async Task ChangeRole_SameRole_IsIdempotent()
    {
        var fixture = await NewFixtureAsync();
        await fixture.Service.CreateUserAsync("bob", UserRole.Operator, "1234", "admin");

        var result = await fixture.Service.ChangeRoleAsync("bob", UserRole.Operator, "admin");

        Assert.True(result.Ok);
    }
}
