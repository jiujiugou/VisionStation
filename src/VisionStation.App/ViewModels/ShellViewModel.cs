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

    public object? CurrentPage => _navigation.CurrentPage;

    public string CurrentRoute => _navigation.CurrentRoute;

    public ShellViewModel(SidebarViewModel sidebar, StatusBarViewModel statusBar, INavigationService navigation)
    {
        Sidebar = sidebar;
        StatusBar = statusBar;
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

        // 离开的页面在这里收尾。当前只有检测页有"编辑态"这种页面级状态，
        // 两个页面时直接判断类型最直白；页面多起来再抽成页面接口。
        if (_shownPage is InspectionPageViewModel leaving)
            leaving.ExitEditMode();

        _shownPage = CurrentPage;
        OnPropertyChanged(nameof(CurrentPage));
    }

    /// <summary>
    /// F2：编辑当前页的 ROI。只有检测页有 ROI 编辑器，其余页面按了不做事。
    /// 放在外壳而不是页面里，是因为快捷键要跟窗口而不是跟焦点元素走 ——
    /// 焦点在侧边栏时，页面上的 InputBinding 收不到按键。
    /// </summary>
    [RelayCommand]
    private void ToggleRoiEditor()
    {
        if (CurrentPage is InspectionPageViewModel page)
            page.ToggleRoiEditorCommand.Execute(null);
    }
}
