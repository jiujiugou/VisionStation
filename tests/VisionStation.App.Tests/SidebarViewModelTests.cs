using VisionStation.App.ViewModels;

namespace VisionStation.App.Tests;

/// <summary>
/// 侧边栏 = 导航的入口。
///
/// 改造后它不再发消息，而是"选中项变了就导航"；点了没实现的页签要把选中项退回去。
/// 这两条以前根本没法测（消息发出去谁收、窗口怎么切 Visibility，都只在真进程里发生）。
/// </summary>
public class SidebarViewModelTests
{
    [Fact]
    public void Constructor_SelectsCurrentRoute()
    {
        var (sidebar, _, _) = Create();

        Assert.Equal("检测", sidebar.SelectedNavItem?.Label);
        Assert.Equal("检测", sidebar.ActivePage);
    }

    [Fact]
    public void SelectingImplementedPage_NavigatesAndUpdatesActivePage()
    {
        var (sidebar, navigation, camera) = Create();

        sidebar.SelectedNavItem = sidebar.BottomItems.Single(i => i.Label == "相机");

        Assert.Contains("相机", navigation.Attempts);
        Assert.Same(camera, navigation.CurrentPage);
        Assert.Equal("相机", sidebar.ActivePage);
    }

    [Fact]
    public void SelectingUnimplementedPage_RevertsSelectionAndKeepsActivePage()
    {
        var (sidebar, navigation, _) = Create();

        sidebar.SelectedNavItem = sidebar.TopItems.Single(i => i.Label == "参数");

        // 导航明确失败 → 高亮退回当前页，不能让用户停在一个打不开的页签上
        Assert.Contains("参数", navigation.Attempts);
        Assert.Equal("检测", sidebar.ActivePage);
        Assert.Equal("检测", sidebar.SelectedNavItem?.Label);
    }

    private static (SidebarViewModel Sidebar, FakeNavigationService Navigation, CameraPageViewModel Camera) Create()
    {
        var inspection = TestPages.Inspection();
        var camera = TestPages.Camera();
        var navigation = new FakeNavigationService("检测", inspection)
            .With("检测", inspection)
            .With("相机", camera);

        return (new SidebarViewModel(navigation), navigation, camera);
    }
}
