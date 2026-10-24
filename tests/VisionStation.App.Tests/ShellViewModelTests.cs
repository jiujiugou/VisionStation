using VisionStation.App.ViewModels;
using VisionStation.Core.Models;

namespace VisionStation.App.Tests;

/// <summary>
/// 外壳契约：切页时通知界面。
///
/// 2026-10-23：随着 ROI 编辑器移除，F2 路由与"离开页面时收尾编辑态"这两条契约一起消失，
/// 对应用例已删（`ToggleRoiEditor_*`、`NavigatingAway_ClosesEditorOnLeavingPage`）。
/// 留下的这条仍然有价值：CurrentPage 变化必须同时通知 CurrentPage 与 CurrentRoute，
/// 少一个都会让侧边栏高亮和内容区不同步。
/// </summary>
public class ShellViewModelTests
{
    [Fact]
    public void CurrentPageChange_RaisesPropertyChanged()
    {
        var (shell, navigation, _, camera) = Create();
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        navigation.Navigate("相机");

        Assert.Contains(nameof(ShellViewModel.CurrentPage), changed);
        Assert.Contains(nameof(ShellViewModel.CurrentRoute), changed);
        Assert.Same(camera, shell.CurrentPage);
    }

    private static (ShellViewModel Shell, FakeNavigationService Navigation,
        InspectionPageViewModel Inspection, CameraPageViewModel Camera) Create()
    {
        var inspection = TestPages.Inspection();
        var camera = TestPages.Camera();
        var navigation = new FakeNavigationService("检测", inspection)
            .With("检测", inspection)
            .With("相机", camera);

        // 顶栏 2026-10-24 加入外壳。它只读配方 + 设备状态，不参与导航，
        // 所以这里给一份最小配方即可 —— 断言仍然是"切页必须通知两处"。
        // 顶栏与侧栏共用同一个会话：真实运行时它们读的就是同一个 ISessionService，
        // 各给一个替身会让"登录后两处状态不一致"这种问题在测试里看不见。
        var session = new FakeSessionService();
        var topBar = new TopBarViewModel(new RecipeConfig(), new FakeTriggerSource(),
            new FakeAuditService(), session, new FakeLoginPrompt());

        var shell = new ShellViewModel(
            new SidebarViewModel(navigation, session),
            new StatusBarViewModel(),
            topBar,
            navigation);

        return (shell, navigation, inspection, camera);
    }
}
