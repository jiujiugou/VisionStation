using VisionStation.App.ViewModels;

namespace VisionStation.App.Tests;

/// <summary>
/// 外壳契约：F2 路由到当前页、切页时给离开的页面收尾。
///
/// F2 从 MainWindow 的 KeyDown 搬到窗口 InputBindings 之后，命令挂在 Shell 上 ——
/// 快捷键要跟窗口走，否则焦点落在侧边栏时页面收不到按键（改造前就是这个问题）。
/// </summary>
public class ShellViewModelTests
{
    [Fact]
    public void ToggleRoiEditor_OnInspectionPage_OpensEditor()
    {
        var (shell, _, inspection, _) = Create();

        shell.ToggleRoiEditorCommand.Execute(null);

        Assert.True(inspection.IsRoiEditorOpen);
    }

    [Fact]
    public void ToggleRoiEditor_OnOtherPage_DoesNothing()
    {
        var (shell, navigation, inspection, camera) = Create();
        navigation.Navigate("相机");

        shell.ToggleRoiEditorCommand.Execute(null);

        // 相机页没有 ROI 编辑器，按 F2 不能把检测页的编辑态打开
        Assert.Same(camera, shell.CurrentPage);
        Assert.False(inspection.IsRoiEditorOpen);
    }

    [Fact]
    public void NavigatingAway_ClosesEditorOnLeavingPage()
    {
        var (shell, navigation, inspection, _) = Create();
        shell.ToggleRoiEditorCommand.Execute(null);
        Assert.True(inspection.IsRoiEditorOpen);

        navigation.Navigate("相机");

        // 覆盖层不能跟着用户漂到相机页上
        Assert.False(inspection.IsRoiEditorOpen);
        Assert.Equal("相机", shell.CurrentRoute);
        Assert.Same(shell.CurrentPage, navigation.CurrentPage);
    }

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

        var shell = new ShellViewModel(new SidebarViewModel(navigation), new StatusBarViewModel(), navigation);
        return (shell, navigation, inspection, camera);
    }
}
