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

    /// <summary>
    /// 上一帧正在显示的页面。字段本身不参与界面逻辑，只为"离开某页时收尾"留一个句柄
    /// （页面级编辑态在 ROI 编辑器移除后已不存在）。当前写法下它等价于 <see cref="CurrentPage"/>。
    /// </summary>
    private object? _shownPage;

    /// <summary>侧边栏（导航按钮 + 角色可见性）。跨页常驻。</summary>
    public SidebarViewModel Sidebar { get; }

    /// <summary>状态栏（用户 / 角色 / 平台灯 / 时间）。跨页常驻。</summary>
    public StatusBarViewModel StatusBar { get; }

    /// <summary>
    /// 顶栏（2026-10-24 加入）。跨整个窗口宽度常驻，不随页面切换消失 ——
    /// 它显示的是"这台设备当前是什么状态"，与"正在看哪一页"无关。
    /// </summary>
    public TopBarViewModel TopBar { get; }

    /// <summary>
    /// 当前页 VM，XAML 里绑到 ContentControl.Content。
    /// 这里是**转发**而不是副本：真正的状态归 <see cref="INavigationService"/>，
    /// 外壳只在它变化时补发一次 PropertyChanged（见 <see cref="OnNavigationChanged"/>）。
    /// </summary>
    public object? CurrentPage => _navigation.CurrentPage;

    /// <summary>当前路由名，侧边栏据此高亮。同样是转发。</summary>
    public string CurrentRoute => _navigation.CurrentRoute;

    /// <summary>
    /// 全部依赖都是常驻单例，构造里不做任何 I/O、不订阅业务消息 ——
    /// 外壳只负责"把三块常驻区域和一个页面容器拼起来"。
    /// </summary>
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
        // 弱引用不适用在这里：事件源是单例服务，订阅者是单例 VM，两者同生共死，
        // 用普通事件即可（用 WeakEventManager 只会让"谁还活着"更难判断）。
        _navigation.PropertyChanged += OnNavigationChanged;
    }

    /// <summary>
    /// 把导航服务的变更翻译成外壳自己的 PropertyChanged。
    /// <para>
    /// 只转发两个属性：CurrentPage / CurrentRoute。导航服务的其他属性（将来若有）
    /// 不经过外壳，避免外壳变成一个"什么都知道"的中转站。
    /// </para>
    /// </summary>
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
