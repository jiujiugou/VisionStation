using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionStation.App.Services.Navigation;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 外壳 ViewModel：侧边栏、状态栏、当前页。
///
/// 它是窗口的唯一 DataContext —— 窗口本身不再认识任何面板，
/// 页面切换靠 <see cref="CurrentPage"/> 的绑定 + <c>DataTemplate</c> 完成。
///
/// 归属划分：
///   - 侧边栏 / 状态栏：跨页常驻，归外壳；
///   - 当前页：归 <see cref="INavigationService"/>，外壳只做转发和收尾；
///   - F2：应用级快捷键，归外壳（它知道"当前页是谁"）。
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private object? _shownPage;

    public SidebarViewModel Sidebar { get; }
    public StatusBarViewModel StatusBar { get; }

    /// <summary>
    /// 顶栏（2026-10-24 加入）。跨整个窗口宽度常驻，不随页面切换消失 ——
    /// 它显示的是"这台设备当前是什么状态"，与"正在看哪一页"无关。
    /// </summary>
    public TopBarViewModel TopBar { get; }

    public object? CurrentPage => _navigation.CurrentPage;

    public string CurrentRoute => _navigation.CurrentRoute;

    public ShellViewModel(
        SidebarViewModel sidebar,
        StatusBarViewModel statusBar,
        TopBarViewModel topBar,
        INavigationService navigation)
    {
        Sidebar = sidebar;
        StatusBar = statusBar;
        TopBar = topBar;
        _navigation = navigation;

        _shownPage = navigation.CurrentPage;
        _navigation.PropertyChanged += OnNavigationChanged;
    }

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(INavigationService.CurrentRoute))
        {
            OnPropertyChanged(nameof(CurrentRoute));
            return;
        }

        if (e.PropertyName != nameof(INavigationService.CurrentPage)) return;

        // 离开的页面在这里收尾。ROI 编辑器移除后，页面级"编辑态"已不存在；
        // 将来若再加（如参数页签），仍在这里统一处理。
        _shownPage = CurrentPage;
        OnPropertyChanged(nameof(CurrentPage));
    }
}
