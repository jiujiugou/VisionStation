using VisionStation.Core.Models;
using VisionStation.Core.Users;
using VisionStation.Service.Audit;
using VisionStation.Service.Security;
using VisionStation.Storage;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// 权限模型：角色到权限位的映射。
///
/// 这些断言看起来琐碎，但它们守的是"谁能干什么"这条线 ——
/// 一旦某个权限位漏给操作员，界面上就会出现一个能点的按钮，
/// 而代码层面不会有任何报错。测试是唯一能拦住它的地方。
/// </summary>
public class PermissionModelTests
{
    [Theory]
    [InlineData(Permission.ViewRun)]
    [InlineData(Permission.StartStop)]
    [InlineData(Permission.AckAlarm)]
    [InlineData(Permission.ViewHistory)]
    public void Operator_HasBasicPermissions(Permission permission)
        => Assert.True(RolePermissions.Has(UserRole.Operator, permission));

    [Theory]
    [InlineData(Permission.EditParameters)]
    [InlineData(Permission.EnterEngineerMode)]
    [InlineData(Permission.CameraSettings)]
    [InlineData(Permission.EditRecipe)]
    [InlineData(Permission.SwitchRecipe)]
    [InlineData(Permission.ManageUsers)]
    [InlineData(Permission.ViewAudit)]
    public void Operator_IsDeniedEverythingAboveBasic(Permission permission)
        => Assert.False(RolePermissions.Has(UserRole.Operator, permission));

    [Fact]
    public void Technician_CanSwitchRecipeButNotEditIt()
    {
        Assert.True(RolePermissions.Has(UserRole.Technician, Permission.SwitchRecipe));
        Assert.True(RolePermissions.Has(UserRole.Technician, Permission.ViewStats));
        Assert.False(RolePermissions.Has(UserRole.Technician, Permission.EditRecipe));
        Assert.False(RolePermissions.Has(UserRole.Technician, Permission.EditParameters));
    }

    [Fact]
    public void Engineer_CanTuneButCannotManagePeopleOrReadAudit()
    {
        Assert.True(RolePermissions.Has(UserRole.Engineer, Permission.EditParameters));
        Assert.True(RolePermissions.Has(UserRole.Engineer, Permission.EnterEngineerMode));
        Assert.True(RolePermissions.Has(UserRole.Engineer, Permission.CameraSettings));

        // 两条边界：能改参数，但不能管人，也不能擦自己的脚印
        Assert.False(RolePermissions.Has(UserRole.Engineer, Permission.ManageUsers));
        Assert.False(RolePermissions.Has(UserRole.Engineer, Permission.ViewAudit));
    }

    [Fact]
    public void Admin_HasEverything()
    {
        foreach (Permission p in Enum.GetValues<Permission>())
            Assert.True(RolePermissions.Has(UserRole.Admin, p), $"管理员缺少 {p}");
    }

    /// <summary>权限是累加继承的：高角色必须包含低角色的全部权限</summary>
    [Fact]
    public void HigherRole_ContainsLowerRolePermissions()
    {
        Assert.Equal(RolePermissions.Of(UserRole.Operator),
            RolePermissions.Of(UserRole.Technician) & RolePermissions.Of(UserRole.Operator));

        Assert.Equal(RolePermissions.Of(UserRole.Technician),
            RolePermissions.Of(UserRole.Engineer) & RolePermissions.Of(UserRole.Technician));

        Assert.Equal(RolePermissions.Of(UserRole.Engineer),
            RolePermissions.Of(UserRole.Admin) & RolePermissions.Of(UserRole.Engineer));
    }

    /// <summary>未知角色（枚举反序列化出意外值）不得放大权限</summary>
    [Fact]
    public void UnknownRole_GetsNoPermissions()
    {
        var weird = (UserRole)99;
        Assert.Equal(Permission.None, RolePermissions.Of(weird));
        Assert.False(RolePermissions.Has(weird, Permission.ViewRun));
    }

    /// <summary>
    /// 急停与停止不需要任何权限。
    ///
    /// 这是安全约束而不是疏忽：权限系统要是把急停挡住，
    /// 出事时会变成"我登不上所以停不了"。
    /// </summary>
    [Fact]
    public void EmergencyStopAndStop_RequireNoPermission()
    {
        Assert.Equal(Permission.None, RolePermissions.RequiresPermission(AuditAction.EStopPressed));
        Assert.Equal(Permission.None, RolePermissions.RequiresPermission(AuditAction.Stop));
    }

    [Theory]
    [InlineData(AuditAction.Start, Permission.StartStop)]
    [InlineData(AuditAction.ModeSwitch, Permission.EnterEngineerMode)]
    [InlineData(AuditAction.EngineReload, Permission.EnterEngineerMode)]
    [InlineData(AuditAction.RecipeSwitch, Permission.SwitchRecipe)]
    [InlineData(AuditAction.ParameterChange, Permission.EditParameters)]
    public void GuardedActions_MapToExpectedPermission(AuditAction action, Permission expected)
        => Assert.Equal(expected, RolePermissions.RequiresPermission(action));
}

/// <summary>密码哈希：盐随机、验证、容错、轮数升级判定</summary>
public class PasswordHasherTests
{
    /// <summary>测试用低轮数 —— 120000 轮跑几百次会很慢，这里验的是逻辑不是强度</summary>
    private const int FastIterations = 1000;

    [Fact]
    public void SamePassword_ProducesDifferentHashes()
    {
        var a = PasswordHasher.Create("1234", FastIterations);
        var b = PasswordHasher.Create("1234", FastIterations);

        Assert.NotEqual(a.Hash, b.Hash);
        Assert.NotEqual(a.Salt, b.Salt);
    }

    [Fact]
    public void Verify_WithCorrectPassword_ReturnsTrue()
    {
        var h = PasswordHasher.Create("abcd1234", FastIterations);
        Assert.True(PasswordHasher.Verify("abcd1234", h.Hash, h.Salt, h.Iterations));
    }

    [Fact]
    public void Verify_WithWrongPassword_ReturnsFalse()
    {
        var h = PasswordHasher.Create("abcd1234", FastIterations);
        Assert.False(PasswordHasher.Verify("abcd1235", h.Hash, h.Salt, h.Iterations));
        Assert.False(PasswordHasher.Verify("ABCD1234", h.Hash, h.Salt, h.Iterations));
    }

    /// <summary>畸形数据返回 false 而不是抛 —— 它在登录路径上</summary>
    [Theory]
    [InlineData("", "AAAA", 1000)]
    [InlineData("AAAA", "", 1000)]
    [InlineData("不是base64", "AAAA", 1000)]
    [InlineData("AAAA", "不是base64", 1000)]
    [InlineData("AAAA", "AAAA", 0)]
    public void Verify_WithMalformedInput_ReturnsFalse(string hash, string salt, int iterations)
        => Assert.False(PasswordHasher.Verify("1234", hash, salt, iterations));

    [Fact]
    public void NeedsUpgrade_OnlyWhenBelowCurrentStandard()
    {
        Assert.True(PasswordHasher.NeedsUpgrade(PasswordHasher.CurrentIterations - 1));
        Assert.False(PasswordHasher.NeedsUpgrade(PasswordHasher.CurrentIterations));
        Assert.False(PasswordHasher.NeedsUpgrade(PasswordHasher.CurrentIterations + 1));
    }
}

/// <summary>
/// 认证：种子账号、失败锁定、停用、轮数自动升级。
///
/// 全部跑在真实 SQLite 临时库上 —— 账号表的映射（枚举存 int、主键是用户名）
/// 只有跑一次才知道对不对。
/// </summary>
public class UserServiceTests
{
    private sealed class RecordingAudit : IAuditService
    {
        public List<(string User, AuditAction Action, AuditResult Result, string Detail)> Entries { get; } = new();

        public void Record(AuditAction action, string target = "", string? before = null,
            string? after = null, AuditResult result = AuditResult.Success, string? detail = null)
            => Entries.Add(("(session)", action, result, detail ?? string.Empty));

        public void RecordAs(string userName, string userRole, AuditAction action, string target = "",
            string? before = null, string? after = null,
            AuditResult result = AuditResult.Success, string? detail = null)
            => Entries.Add((userName, action, result, detail ?? string.Empty));

        public Task<IReadOnlyList<AuditEntry>> QueryRecentAsync(int count = 200)
            => Task.FromResult<IReadOnlyList<AuditEntry>>(Array.Empty<AuditEntry>());

        public Task<IReadOnlyList<AuditEntry>> QueryAsync(
            AuditAction? action = null, string? userName = null, DateTime? since = null, int count = 200)
            => Task.FromResult<IReadOnlyList<AuditEntry>>(Array.Empty<AuditEntry>());

        public void Flush(TimeSpan timeout) { }
    }

    private sealed class Context : IAsyncDisposable
    {
        public required UserService Service { get; init; }
        public required UserRepository Repo { get; init; }
        public required RecordingAudit Audit { get; init; }
        public required string DbPath { get; init; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<Context> NewContextAsync(Func<DateTime>? now = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs_user_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "visionstation.db");

        var repo = new UserRepository(dbPath);
        await repo.EnsureCreatedAsync();

        var audit = new RecordingAudit();
        var svc = new UserService(repo, audit, logger: null, now: now);

        return new Context { Service = svc, Repo = repo, Audit = audit, DbPath = dbPath };
    }

    [Fact]
    public async Task EnsureDefaultAdmin_CreatesAdminWithMustChangePassword()
    {
        var ctx = await NewContextAsync();

        await ctx.Service.EnsureDefaultAdminAsync();

        var admin = await ctx.Repo.FindAsync(UserService.DefaultAdminUserName);
        Assert.NotNull(admin);
        Assert.Equal(UserRole.Admin, admin.Role);
        Assert.True(admin.MustChangePassword, "出厂密码必须强制修改");
        Assert.True(admin.IsEnabled);

        // 审计里要留一条"创建了出厂管理员"
        Assert.Contains(ctx.Audit.Entries, e => e.Action == AuditAction.UserCreate);
    }

    /// <summary>已经有账号时不得覆盖 —— 否则重启一次就把改过的密码重置回出厂值</summary>
    [Fact]
    public async Task EnsureDefaultAdmin_DoesNotOverwriteExisting()
    {
        var ctx = await NewContextAsync();
        await ctx.Service.EnsureDefaultAdminAsync();

        var changed = PasswordHasher.Create("newpass", 1000);
        await ctx.Repo.UpdatePasswordAsync("admin", changed.Hash, changed.Salt, changed.Iterations, false);

        await ctx.Service.EnsureDefaultAdminAsync();

        var admin = await ctx.Repo.FindAsync("admin");
        Assert.False(admin!.MustChangePassword);
        Assert.True(PasswordHasher.Verify("newpass", admin.PasswordHash, admin.PasswordSalt, admin.PasswordIterations));
    }

    [Fact]
    public async Task Authenticate_WithCorrectPassword_Succeeds()
    {
        var ctx = await NewContextAsync();
        await ctx.Service.EnsureDefaultAdminAsync();

        var result = await ctx.Service.AuthenticateAsync("admin", UserService.DefaultAdminPassword);

        Assert.True(result.Succeeded);
        Assert.Equal(UserRole.Admin, result.User!.Role);
        Assert.Contains(ctx.Audit.Entries, e => e.Action == AuditAction.Login && e.Result == AuditResult.Success);
    }

    [Fact]
    public async Task Authenticate_IsCaseInsensitiveOnUserName()
    {
        var ctx = await NewContextAsync();
        await ctx.Service.EnsureDefaultAdminAsync();

        var result = await ctx.Service.AuthenticateAsync("ADMIN", UserService.DefaultAdminPassword);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Authenticate_WithWrongPassword_RecordsDenied()
    {
        var ctx = await NewContextAsync();
        await ctx.Service.EnsureDefaultAdminAsync();

        var result = await ctx.Service.AuthenticateAsync("admin", "wrong");

        Assert.False(result.Succeeded);
        Assert.Equal(AuthOutcome.WrongPassword, result.Outcome);
        Assert.Contains(ctx.Audit.Entries,
            e => e.Action == AuditAction.LoginFailed && e.Result == AuditResult.Denied);
    }

    /// <summary>
    /// 连续失败达阈值后锁定，且锁定期内即使密码正确也拒绝。
    ///
    /// 这条是防在线爆破的核心：如果只看密码对不对，
    /// 攻击者可以无限次尝试，"失败计数"就只是个装饰。
    /// </summary>
    [Fact]
    public async Task Authenticate_AfterThreeFailures_LocksEvenWithCorrectPassword()
    {
        var now = new DateTime(2026, 10, 24, 9, 0, 0);
        var ctx = await NewContextAsync(() => now);
        await ctx.Service.EnsureDefaultAdminAsync();

        for (var i = 0; i < UserService.MaxFailuresBeforeLock; i++)
            await ctx.Service.AuthenticateAsync("admin", "wrong");

        // 锁定期内：正确密码也进不去
        var locked = await ctx.Service.AuthenticateAsync("admin", UserService.DefaultAdminPassword);
        Assert.False(locked.Succeeded);
        Assert.Equal(AuthOutcome.LockedOut, locked.Outcome);
        Assert.NotNull(locked.LockRemaining);

        // 锁定到期后恢复
        now = now.Add(UserService.LockDuration).AddSeconds(1);
        var after = await ctx.Service.AuthenticateAsync("admin", UserService.DefaultAdminPassword);
        Assert.True(after.Succeeded);
    }

    [Fact]
    public async Task Authenticate_OnDisabledAccount_IsRejected()
    {
        var ctx = await NewContextAsync();
        await ctx.Service.EnsureDefaultAdminAsync();
        await ctx.Repo.SetEnabledAsync("admin", false);

        var result = await ctx.Service.AuthenticateAsync("admin", UserService.DefaultAdminPassword);

        Assert.False(result.Succeeded);
        Assert.Equal(AuthOutcome.Disabled, result.Outcome);
    }

    /// <summary>
    /// 登录成功后自动把低轮数哈希升级到当前标准 —— 用户不需要做任何事。
    /// </summary>
    [Fact]
    public async Task Authenticate_UpgradesOutdatedIterations()
    {
        var ctx = await NewContextAsync();

        var weak = PasswordHasher.Create("1234", 1000);
        await ctx.Repo.UpsertAsync(new UserAccount
        {
            UserName = "op01",
            Role = UserRole.Operator,
            PasswordHash = weak.Hash,
            PasswordSalt = weak.Salt,
            PasswordIterations = weak.Iterations,
        });

        var result = await ctx.Service.AuthenticateAsync("op01", "1234");
        Assert.True(result.Succeeded);

        var after = await ctx.Repo.FindAsync("op01");
        Assert.Equal(PasswordHasher.CurrentIterations, after!.PasswordIterations);
        // 升级后仍能用同一个密码登录
        Assert.True(PasswordHasher.Verify("1234", after.PasswordHash, after.PasswordSalt, after.PasswordIterations));
    }

    [Theory]
    [InlineData("1234", "12")]      // 新密码太短
    [InlineData("1234", "")]        // 新密码为空
    [InlineData("1234", "1234")]    // 新密码与当前相同
    public async Task ChangePassword_RejectsInvalidNewPassword(string current, string next)
    {
        var ctx = await NewContextAsync();

        // 账号的当前密码就是 current
        var pwd = PasswordHasher.Create(current, 1000);
        await ctx.Repo.UpsertAsync(new UserAccount
        {
            UserName = "op01",
            PasswordHash = pwd.Hash,
            PasswordSalt = pwd.Salt,
            PasswordIterations = pwd.Iterations,
        });

        var (ok, error) = await ctx.Service.ChangePasswordAsync("op01", current, next);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }

    /// <summary>当前密码输错时不得改密 —— 否则有人趁着登录态顺手把密码换掉</summary>
    [Fact]
    public async Task ChangePassword_WithWrongCurrentPassword_IsRejected()
    {
        var ctx = await NewContextAsync();

        var pwd = PasswordHasher.Create("1234", 1000);
        await ctx.Repo.UpsertAsync(new UserAccount
        {
            UserName = "op01",
            PasswordHash = pwd.Hash,
            PasswordSalt = pwd.Salt,
            PasswordIterations = pwd.Iterations,
        });

        var (ok, error) = await ctx.Service.ChangePasswordAsync("op01", "9999", "brandnew1");

        Assert.False(ok);
        Assert.Equal("当前密码不正确", error);
    }

    [Fact]
    public async Task ChangePassword_WithRightCurrentPassword_SucceedsAndClearsMustChange()
    {
        var ctx = await NewContextAsync();
        await ctx.Service.EnsureDefaultAdminAsync();

        var (ok, error) = await ctx.Service.ChangePasswordAsync(
            "admin", UserService.DefaultAdminPassword, "newpass99");

        Assert.True(ok, error);

        var admin = await ctx.Repo.FindAsync("admin");
        Assert.False(admin!.MustChangePassword);
        Assert.Contains(ctx.Audit.Entries, e => e.Action == AuditAction.PasswordChange);
    }
}

/// <summary>
/// 会话：降级与班次登出。
///
/// 时间源注入进来，所以这些"5 分钟""8 小时"的断言是瞬时完成的，
/// 不用真的等 —— 这也是 <see cref="SessionService"/> 接受
/// <c>Func&lt;DateTime&gt;</c> 的唯一理由。
/// </summary>
public class SessionServiceTests
{
    private static UserAccount MakeUser(UserRole role) => new()
    {
        UserName = "op01",
        DisplayName = "张三",
        Role = role,
        IsEnabled = true,
    };

    [Fact]
    public void Start_SetsEffectiveRoleAndAuthenticated()
    {
        var session = new SessionService(() => new DateTime(2026, 10, 24, 8, 0, 0), startTimer: false);

        session.Start(MakeUser(UserRole.Engineer));

        Assert.True(session.IsAuthenticated);
        Assert.Equal(UserRole.Engineer, session.EffectiveRole);
        Assert.True(session.HasPermission(Permission.EditParameters));
    }

    [Fact]
    public void IdleBeyondTimeout_DowngradesToOperator()
    {
        var now = new DateTime(2026, 10, 24, 8, 0, 0);
        var session = new SessionService(() => now, startTimer: false);
        session.Start(MakeUser(UserRole.Engineer));

        now = now.Add(SessionService.IdleTimeout).AddSeconds(1);
        session.CheckTimeouts();

        // 仍处于登录态，但权限已经收回
        Assert.True(session.IsAuthenticated);
        Assert.Equal(UserRole.Operator, session.EffectiveRole);
        Assert.False(session.HasPermission(Permission.EditParameters));
        Assert.True(session.HasPermission(Permission.StartStop));
    }

    /// <summary>Touch 重置计时：人一直在操作就不该被降级</summary>
    [Fact]
    public void Touch_ResetsIdleTimer()
    {
        var now = new DateTime(2026, 10, 24, 8, 0, 0);
        var session = new SessionService(() => now, startTimer: false);
        session.Start(MakeUser(UserRole.Engineer));

        now = now.AddMinutes(4);
        session.Touch();

        now = now.AddMinutes(4);   // 距登录 8 分钟，但距上次操作只有 4 分钟
        session.CheckTimeouts();

        Assert.Equal(UserRole.Engineer, session.EffectiveRole);
    }

    [Fact]
    public void AfterDowngrade_RaisesChangeEvent()
    {
        var now = new DateTime(2026, 10, 24, 8, 0, 0);
        var session = new SessionService(() => now, startTimer: false);
        SessionChangeKind? kind = null;
        session.Changed += (_, e) => kind = e.Kind;

        session.Start(MakeUser(UserRole.Engineer));
        now = now.Add(SessionService.IdleTimeout).AddSeconds(1);
        session.CheckTimeouts();

        Assert.Equal(SessionChangeKind.Downgraded, kind);
    }

    [Fact]
    public void AfterShiftDuration_LogsOut()
    {
        var now = new DateTime(2026, 10, 24, 8, 0, 0);
        var session = new SessionService(() => now, startTimer: false);
        session.Start(MakeUser(UserRole.Admin));

        now = now.Add(SessionService.ShiftDuration).AddSeconds(1);
        session.CheckTimeouts();

        Assert.False(session.IsAuthenticated);
        Assert.Null(session.CurrentUser);
    }

    /// <summary>降级不延长班次：8 小时是从登录算起，不是从最后一次操作算起</summary>
    [Fact]
    public void Touch_DoesNotExtendShift()
    {
        var now = new DateTime(2026, 10, 24, 8, 0, 0);
        var session = new SessionService(() => now, startTimer: false);
        session.Start(MakeUser(UserRole.Admin));

        now = now.AddHours(7);
        session.Touch();
        now = now.AddHours(2);     // 距登录 9 小时
        session.CheckTimeouts();

        Assert.False(session.IsAuthenticated);
    }

    [Fact]
    public void NotLoggedIn_HasNoPermissions()
    {
        var session = new SessionService(() => DateTime.Now, startTimer: false);

        Assert.False(session.IsAuthenticated);
        Assert.False(session.HasPermission(Permission.ViewRun));
        Assert.False(session.HasPermission(Permission.StartStop));

        // None 视为"不需要权限"，恒为真
        Assert.True(session.HasPermission(Permission.None));
    }

    [Fact]
    public void Logout_RaisesEventAndClearsUser()
    {
        var session = new SessionService(() => DateTime.Now, startTimer: false);
        session.Start(MakeUser(UserRole.Admin));

        SessionChangeKind? kind = null;
        session.Changed += (_, e) => kind = e.Kind;

        session.Logout("主动退出");

        Assert.Equal(SessionChangeKind.Logout, kind);
        Assert.False(session.IsAuthenticated);
    }
}
