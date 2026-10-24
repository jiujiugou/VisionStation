using VisionStation.App.ViewModels;
using VisionStation.Core.Models;
using VisionStation.Core.Users;
using Xunit;

namespace VisionStation.App.Tests;

/// <summary>
/// 顶栏的权限门控。
///
/// 这些用例守的是一条很容易被绕过、而且不会报错的线：
/// 没权限的人点了按钮，程序"看起来"照做了，只是权限检查被写在注释里。
/// 只有断言"没进去 + 记了拒绝"，才能证明那道门真的存在。
/// </summary>
public class TopBarPermissionTests
{
    private static (TopBarViewModel TopBar, FakeSessionService Session, FakeLoginPrompt Prompt, FakeAuditService Audit)
        Create()
    {
        var session = new FakeSessionService();
        var prompt = new FakeLoginPrompt();
        var audit = new FakeAuditService();
        var topBar = new TopBarViewModel(new RecipeConfig(), new FakeTriggerSource(), audit, session, prompt);
        return (topBar, session, prompt, audit);
    }

    /// <summary>未登录点"切换模式"：弹登录，被取消就什么都不该发生</summary>
    [Fact]
    public void ToggleMode_WhenLoginCancelled_StaysInRunMode()
    {
        var (topBar, _, prompt, audit) = Create();
        prompt.NextResult = false;

        topBar.ToggleModeCommand.Execute(null);

        Assert.False(topBar.IsEngineerMode);
        Assert.Equal(1, prompt.ShowCount);
        Assert.Contains(audit.Entries, e => e.Result == AuditResult.Denied);
    }

    /// <summary>弹窗里用工程师登录 → 放行</summary>
    [Fact]
    public void ToggleMode_WhenLoginGrantsEngineer_EntersEngineerMode()
    {
        var (topBar, session, prompt, _) = Create();
        prompt.NextResult = true;
        prompt.OnShown = () => session.SignIn(UserRole.Engineer);

        topBar.ToggleModeCommand.Execute(null);

        Assert.True(topBar.IsEngineerMode);
    }

    /// <summary>弹窗里用操作员登录 → 仍是运行模式（没权限就是没权限）</summary>
    [Fact]
    public void ToggleMode_WhenOperatorLogsIn_StaysInRunMode()
    {
        var (topBar, session, prompt, _) = Create();
        prompt.NextResult = true;
        prompt.OnShown = () => session.SignIn(UserRole.Operator);

        topBar.ToggleModeCommand.Execute(null);

        Assert.False(topBar.IsEngineerMode);
    }

    /// <summary>已经以工程师登录：直接切，不该再弹一次登录</summary>
    [Fact]
    public void ToggleMode_WhenAlreadyEngineer_DoesNotPromptAgain()
    {
        var (topBar, session, prompt, _) = Create();
        session.SignIn(UserRole.Engineer);

        topBar.ToggleModeCommand.Execute(null);

        Assert.True(topBar.IsEngineerMode);
        Assert.Equal(0, prompt.ShowCount);
    }

    /// <summary>
    /// 降级后必须自动退出工程师模式。
    ///
    /// 这是最容易漏的一条：权限收回了但侧栏还挂着相机页、控制面板还全开着，
    /// 就是"能看见就能点"的漏洞 —— 权限系统等于白做。
    /// </summary>
    [Fact]
    public void Downgrade_ExitsEngineerModeAutomatically()
    {
        var (topBar, session, _, _) = Create();
        session.SignIn(UserRole.Engineer);
        topBar.ToggleModeCommand.Execute(null);
        Assert.True(topBar.IsEngineerMode);

        session.Downgrade();

        Assert.False(topBar.IsEngineerMode);
    }

    /// <summary>登出同样要退回运行模式</summary>
    [Fact]
    public void Logout_ExitsEngineerMode()
    {
        var (topBar, session, _, _) = Create();
        session.SignIn(UserRole.Admin);
        topBar.ToggleModeCommand.Execute(null);
        Assert.True(topBar.IsEngineerMode);

        topBar.LogoutCommand.Execute(null);

        Assert.False(topBar.IsEngineerMode);
        Assert.False(topBar.IsAuthenticated);
    }

    /// <summary>进出各一次，全程不该弹登录 —— 权限系统只拦"往上走"</summary>
    [Fact]
    public void LeavingEngineerMode_NeverPrompts()
    {
        var (topBar, session, prompt, _) = Create();
        session.SignIn(UserRole.Engineer);

        topBar.ToggleModeCommand.Execute(null);    // 进（有权限，不弹）
        Assert.True(topBar.IsEngineerMode);

        topBar.ToggleModeCommand.Execute(null);    // 出（不需要权限）

        Assert.False(topBar.IsEngineerMode);
        Assert.Equal(0, prompt.ShowCount);
    }

    /// <summary>登录后用户区要显示名字与角色</summary>
    [Fact]
    public void UserText_ReflectsLogin()
    {
        var (topBar, session, _, _) = Create();
        Assert.Equal("未登录", topBar.UserText);

        session.SignIn(UserRole.Engineer, "zhangsan");

        Assert.Contains("zhangsan", topBar.UserText);
        Assert.Contains("工程师", topBar.UserText);
        Assert.True(topBar.IsAuthenticated);
    }

    /// <summary>主动登出要写审计（自动超时那条由 SessionBridge 记，两边不能都不记）</summary>
    [Fact]
    public void Logout_RecordsAuditUnderTheUser()
    {
        var (topBar, session, _, audit) = Create();
        session.SignIn(UserRole.Engineer, "zhangsan");

        topBar.LogoutCommand.Execute(null);

        Assert.Contains(audit.RecordedAs,
            r => r.User == "zhangsan" && r.Action == AuditAction.Logout);
    }
}
