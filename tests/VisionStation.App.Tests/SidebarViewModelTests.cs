using Microsoft.Extensions.Logging.Abstractions;
using VisionStation.App.Services.Navigation;
using VisionStation.App.ViewModels;
using VisionStation.Core.Users;

namespace VisionStation.App.Tests;

/// <summary>
/// 侧边栏 = 导航的入口。
///
/// 改造后它不持有"选中项"：高亮是 <see cref="NavItem.IsActive"/>，由当前路由单向同步。
/// 这么改的直接原因是一个实测出来的缺陷 —— 两个 ListBox 共享一个 SelectedItem 时，
/// 各自记住自己的选中项、互不清除，于是**同时亮两个图标**，用户判断不出当前页。
/// 那个缺陷在真 WPF 里可复现（临时探针：绑定两块 ListBox 到同一属性，
/// 点下半部『相机』后上半部『检测』仍为 SelectedItem），VM 层测不出来，
/// 所以这里测的是"高亮是否唯一且跟随路由"这个可观察结果。
///
/// 旧的 <c>SelectingUnimplementedPage_RevertsSelectionAndKeepsActivePage</c> 已随
/// 6 个未实现页签一起删除：界面上不再有假按钮，也就没有"退回去"这回事。
/// 取而代之的是 <see cref="EveryNavItem_NavigatesToAnImplementedRoute"/> ——
/// 它会拦住"再往侧边栏塞一个点不动的图标"。
/// </summary>
public class SidebarViewModelTests
{
    [Fact]
    public void Constructor_HighlightsCurrentRoute()
    {
        var (sidebar, _, _) = Create();

        Assert.Equal(NavigationService.InspectionRoute, sidebar.ActivePage);
        Assert.True(InspectionItem(sidebar).IsActive);
        Assert.False(CameraItem(sidebar).IsActive);
    }

    [Fact]
    public void NavigateCommand_SwitchesPageAndMovesHighlightWithoutLeavingTwoLit()
    {
        var (sidebar, navigation, camera) = Create();

        sidebar.NavigateCommand.Execute(CameraItem(sidebar));

        Assert.Contains(NavigationService.CameraRoute, navigation.Attempts);
        Assert.Same(camera, navigation.CurrentPage);
        Assert.Equal(NavigationService.CameraRoute, sidebar.ActivePage);

        // 关键断言：同一时刻只能有一个图标是亮的
        Assert.True(CameraItem(sidebar).IsActive);
        Assert.False(InspectionItem(sidebar).IsActive);
        Assert.Single(AllItems(sidebar).Where(i => i.IsActive));
    }

    [Fact]
    public void NavigateCommand_ToUnroutablePage_KeepsHighlightOnCurrentPage()
    {
        // 相机页没登记 → 导航失败。高亮必须留在检测上，不能凭空挪走。
        var inspection = TestPages.Inspection();
        var navigation = new FakeNavigationService(NavigationService.InspectionRoute, inspection)
            .With(NavigationService.InspectionRoute, inspection);
        var sidebar = new SidebarViewModel(navigation, new FakeSessionService());

        sidebar.NavigateCommand.Execute(CameraItem(sidebar));

        Assert.Equal(NavigationService.InspectionRoute, sidebar.ActivePage);
        Assert.True(InspectionItem(sidebar).IsActive);
        Assert.False(CameraItem(sidebar).IsActive);
    }

    [Fact]
    public void EveryNavItem_NavigatesToAnImplementedRoute()
    {
        // 守卫：侧边栏上摆出来的每一个图标，都必须真能点进去。
        // 以前 8 个图标里 6 个点了弹回去，操作员点名骂的就是这个。
        var inspection = TestPages.Inspection();
        var camera = TestPages.Camera();
        var navigation = new NavigationService(inspection, camera,
            new AuditPageViewModel(new FakeAuditService()), NullLogger<NavigationService>.Instance);
        var sidebar = new SidebarViewModel(navigation, new FakeSessionService());

        foreach (var item in AllItems(sidebar))
        {
            Assert.True(navigation.Navigate(item.Route),
                $"侧边栏项「{item.Label}」的路由 {item.Route} 没有对应页面，属于假按钮");
        }
    }

    [Fact]
    public void RunMode_HidesEngineerOnlyNavItems()
    {
        // 运行模式是默认模式。相机参数这类页面对操作员没有意义，
        // 摆在导航里只会让人误点进去、看到一堆看不懂的按钮。
        var (sidebar, _, _) = Create();

        Assert.Empty(sidebar.VisibleBottomItems);
        Assert.DoesNotContain(sidebar.VisibleBottomItems, item => item.EngineerOnly);
    }

    [Fact]
    public void EngineerMode_ShowsEngineerOnlyNavItems()
    {
        var (sidebar, _, _) = Create();

        sidebar.ApplyEngineerMode(true);

        Assert.Contains(sidebar.VisibleBottomItems, item => item.Route == NavigationService.CameraRoute);
    }

    [Fact]
    public void SwitchBackToRunMode_WhileOnEngineerPage_FallsBackToInspection()
    {
        // 悬空状态：切开到运行模式时人正站在相机页上。
        // 不退回的话，导航栏一个高亮都没有，内容区还显示着相机参数。
        var (sidebar, navigation, _) = Create();
        sidebar.ApplyEngineerMode(true);
        sidebar.NavigateCommand.Execute(CameraItem(sidebar));
        Assert.Equal(NavigationService.CameraRoute, navigation.CurrentRoute);

        sidebar.ApplyEngineerMode(false);

        Assert.Equal(NavigationService.InspectionRoute, navigation.CurrentRoute);
        Assert.True(InspectionItem(sidebar).IsActive);
        Assert.Empty(sidebar.VisibleBottomItems);
    }

    private static IEnumerable<NavItem> AllItems(SidebarViewModel sidebar)
        => sidebar.TopItems.Concat(sidebar.BottomItems);

    private static NavItem InspectionItem(SidebarViewModel sidebar)
        => sidebar.TopItems.Single(i => i.Route == NavigationService.InspectionRoute);

    private static NavItem CameraItem(SidebarViewModel sidebar)
        => sidebar.BottomItems.Single(i => i.Route == NavigationService.CameraRoute);

    private static (SidebarViewModel Sidebar, FakeNavigationService Navigation, CameraPageViewModel Camera) Create()
    {
        var inspection = TestPages.Inspection();
        var camera = TestPages.Camera();
        var navigation = new FakeNavigationService(NavigationService.InspectionRoute, inspection)
            .With(NavigationService.InspectionRoute, inspection)
            .With(NavigationService.CameraRoute, camera);

        return (new SidebarViewModel(navigation, new FakeSessionService()), navigation, camera);
    }

    /// <summary>带会话的构造：验"审计入口按权限出现/消失"要用它</summary>
    private static (SidebarViewModel Sidebar, FakeSessionService Session, FakeNavigationService Navigation)
        CreateWithSession()
    {
        var inspection = TestPages.Inspection();
        var navigation = new FakeNavigationService(NavigationService.InspectionRoute, inspection)
            .With(NavigationService.InspectionRoute, inspection)
            .With(NavigationService.CameraRoute, TestPages.Camera())
            .With(NavigationService.AuditRoute, new object());

        var session = new FakeSessionService();
        return (new SidebarViewModel(navigation, session), session, navigation);
    }

    private static bool HasAuditItem(SidebarViewModel sidebar)
        => sidebar.VisibleBottomItems.Any(i => i.Route == NavigationService.AuditRoute);

    /// <summary>没登录：审计入口不出现（它是管理员的页面，不是公开页面）</summary>
    [Fact]
    public void AuditItem_HiddenWhenNotLoggedIn()
    {
        var (sidebar, _, _) = CreateWithSession();

        Assert.False(HasAuditItem(sidebar));
    }

    [Fact]
    public void AuditItem_AppearsForAdmin()
    {
        var (sidebar, session, _) = CreateWithSession();

        session.SignIn(UserRole.Admin);

        Assert.True(HasAuditItem(sidebar));
    }

    /// <summary>
    /// 工程师看不到审计 —— 这条边界是刻意的：
    /// 能改参数的人不该能查"我改参数这件事有没有被记下来"。
    /// </summary>
    [Fact]
    public void AuditItem_HiddenForEngineer()
    {
        var (sidebar, session, _) = CreateWithSession();

        session.SignIn(UserRole.Engineer);

        Assert.False(HasAuditItem(sidebar));
    }

    /// <summary>登出时人正停在审计页：必须退回检测页，不能留在已隐藏的页上</summary>
    [Fact]
    public void Logout_WhileOnAuditPage_ReturnsToInspection()
    {
        var (sidebar, session, _) = CreateWithSession();
        session.SignIn(UserRole.Admin);

        var auditItem = sidebar.BottomItems.Single(i => i.Route == NavigationService.AuditRoute);
        sidebar.NavigateCommand.Execute(auditItem);
        Assert.Equal(NavigationService.AuditRoute, sidebar.ActivePage);

        session.Logout();

        Assert.Equal(NavigationService.InspectionRoute, sidebar.ActivePage);
        Assert.False(HasAuditItem(sidebar));
    }
}
