using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.Services.Navigation;
using VisionStation.Core.Users;
using VisionStation.Service.Security;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 侧边导航栏 ViewModel。
///
/// 职责：
///   1. 持有导航项（上半部：检测；下半部：相机）
///   2. 点图标 → 请求导航；高亮 = 当前路由的投影，不是控件自己的选中项
///   3. 订阅 TriggerStateMessage → 自动模式暗化、停止时恢复
///
/// 为什么没有“选中项”这个概念了：
///   改造前用两个 <c>ListBox</c> 共享一个 <c>SelectedItem</c>。实测（真 WPF，见
///   docs/WPF规范落地记录.md）两个列表各自记住自己的选中项、互不清除，
///   结果**同时有两个图标是高亮的** —— 点下半部的相机，上半部的检测还亮着，
///   操作员没法判断"我现在在哪一页"。这不是绑定写错，是"选中"本身不该由控件拥有：
///   导航状态归 <see cref="INavigationService"/>，图标只是把它画出来。
///   所以这里改成按钮 + <see cref="NavItem.IsActive"/>，唯一真相源是 CurrentRoute。
/// </summary>
public partial class SidebarViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly ISessionService _session;
    private readonly IReadOnlyList<NavItem> _allItems;

    /// <summary>当前是否工程师模式（由顶栏广播驱动）</summary>
    private bool _isEngineerMode;

    /// <summary>是否暗化（自动运行时 true，手动/停止时 false）</summary>
    [ObservableProperty]
    private bool _isDimmed;

    /// <summary>当前页路由，供状态栏/走查断言用</summary>
    public string ActivePage => _navigation.CurrentRoute;

    /// <summary>上部导航项</summary>
    public IReadOnlyList<NavItem> TopItems { get; }

    /// <summary>下部导航项（全量，含工程师专属）</summary>
    public IReadOnlyList<NavItem> BottomItems { get; }

    /// <summary>
    /// 下部导航的可见子集 —— 视图绑这个，不绑 <see cref="BottomItems"/>。
    ///
    /// 为什么不用 CollectionViewSource 过滤：Filter 是个委托，模式一变要 Refresh，
    /// 而 Refresh 会让按钮重建、打断 ToolTip 与键盘焦点。这里项数是个位数，
    /// 直接换集合更简单也更可控。
    /// </summary>
    public ObservableCollection<NavItem> VisibleBottomItems { get; } = new();

    public SidebarViewModel(INavigationService navigation, ISessionService session)
    {
        _navigation = navigation;
        _session = session;

        // 只放真有页面的项。参数/配方/PLC/日志/系统还没有实现，
        // 摆上去就是"点了没反应"的假按钮 —— 宁可侧边栏空着，也不给操作员
        // 一个骗人的入口。等页面真做出来再往这两个列表里加。
        TopItems = new[]
        {
            new NavItem(
                route: NavigationService.InspectionRoute,
                label: "检测",
                geometry: "M12 2C6.5 2 2 6.5 2 12s4.5 10 10 10 10-4.5 10-10S17.5 2 12 2zm-2 15l-5-5 1.4-1.4 3.6 3.6 7.6-7.6L19 8l-9 9z"),
        };

        BottomItems = new[]
        {
            new NavItem(
                route: NavigationService.CameraRoute,
                label: "相机",
                geometry: "M9 2L7.2 4H4a2 2 0 00-2 2v12a2 2 0 002 2h16a2 2 0 002-2V6a2 2 0 00-2-2h-3.2L15 2H9zm3 15a5 5 0 110-10 5 5 0 010 10z M12 8.8a3.2 3.2 0 100 6.4 3.2 3.2 0 000-6.4z",
                engineerOnly: true),
            new NavItem(
                route: NavigationService.HistoryRoute,
                label: "历史",
                geometry: "M13 3c-4.97 0-9 4.03-9 9H1l3.89 3.89.07.14L9 12H6c0-3.87 3.13-7 7-7s7 3.13 7 7-3.13 7-7 7c-1.93 0-3.68-.79-4.94-2.06l-1.42 1.42A8.954 8.954 0 0013 21c4.97 0 9-4.03 9-9s-4.03-9-9-9zm-1 5v5l4.28 2.54.72-1.21-3.5-2.08V8H12z",
                // 操作员也有 ViewHistory：发现 NG 之后回看那一件，是他的活也不是他的错。
                // 与"审计"的区别：历史看的是产品，审计看的是人。
                requiredPermission: Permission.ViewHistory),
            new NavItem(
                route: NavigationService.AuditRoute,
                label: "审计",
                geometry: "M12 1L3 5v6c0 5.55 3.84 10.74 9 12 5.16-1.26 9-6.45 9-12V5l-9-4zm-1 15l-4-4 1.4-1.4L11 13.2l4.6-4.6L17 10l-6 6z",
                // 只给管理员：工程师能改参数，但不该能查审计 ——
                // 否则等于让他确认自己的脚印有没有被记下来
                requiredPermission: Permission.ViewAudit),
        };

        _allItems = TopItems.Concat(BottomItems).ToArray();

        // 运行模式开局：只显示非工程师项。顶栏切模式时再重建。
        ApplyEngineerMode(isEngineerMode: false);

        WeakReferenceMessenger.Default.Register<SidebarViewModel, EngineerModeMessage>(this,
            static (vm, msg) => vm.ApplyEngineerMode(msg.Value));

        WeakReferenceMessenger.Default.Register<SidebarViewModel, TriggerStateMessage>(this,
            (vm, msg) => vm.IsDimmed = msg.Value);

        // 会话变化：登录成管理员时"审计"才出现，登出时又消失。
        // 不刷新的话，登出后侧栏还挂着审计入口 —— 点进去导航到已隐藏的页。
        WeakReferenceMessenger.Default.Register<SidebarViewModel, SessionChangedMessage>(this,
            static (vm, _) => vm.RefreshVisibility());

        // 导航状态是唯一真相源：谁改了路由（侧边栏、快捷键、将来的外部触发），
        // 高亮都跟着走，不需要在每个入口各写一份同步代码。
        _navigation.PropertyChanged += OnNavigationChanged;
        SyncActiveState();
    }

    /// <summary>
    /// 点图标导航。
    ///
    /// 失败（路由没实现）就什么都不做：没有"选中项"要退回去，
    /// 高亮压根没变过 —— 原来那个 <c>_revertingSelection</c> 防回环标志
    /// 连同它守护的整段退回逻辑一起没了。
    /// </summary>
    [RelayCommand]
    private void Navigate(NavItem? item)
    {
        if (item is null) return;
        _navigation.Navigate(item.Route);
    }

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(INavigationService.CurrentRoute)
            or nameof(INavigationService.CurrentPage))
        {
            SyncActiveState();
        }
    }

    /// <summary>
    /// 按模式重建可见导航项。
    ///
    /// 顺带处理一个悬空状态：如果当前正停在即将被隐藏的页上（切开到运行模式时人在相机页），
    /// 要退回检测页 —— 否则导航栏里没有任何高亮项，内容区却还显示着相机参数，
    /// 操作员会以为"我就在这一页"，实际上导航已经没有它了。
    ///
    /// 公开而不是私有：顶栏通过 <see cref="EngineerModeMessage"/> 触发它，
    /// 但测试要能直接摆布模式，不必发全局消息（那个 messenger 是静态的，
    /// 在并行测试里会成为隐式的共享状态）。
    /// </summary>
    public void ApplyEngineerMode(bool isEngineerMode)
    {
        _isEngineerMode = isEngineerMode;
        RebuildVisible();

        // 当前停在的页若已不可见，必须退回检测页：
        // 退出工程师模式时人可能停在相机页；登出时人可能停在审计页。
        // 不退的话侧栏没有任何高亮项，内容区却还显示着那一页。
        var current = _navigation.CurrentRoute;
        var currentItem = _allItems.FirstOrDefault(i =>
            string.Equals(i.Route, current, StringComparison.Ordinal));

        if (currentItem is not null && !IsVisible(currentItem))
            _navigation.Navigate(NavigationService.InspectionRoute);

        SyncActiveState();
    }

    /// <summary>
    /// 会话变化后重新算可见项。
    /// 登录/登出会改变"审计"这一项是否出现 —— 它跟模式无关，只跟权限有关。
    /// </summary>
    public void RefreshVisibility() => ApplyEngineerMode(_isEngineerMode);

    private void RebuildVisible()
    {
        VisibleBottomItems.Clear();
        foreach (var item in BottomItems)
        {
            if (IsVisible(item))
                VisibleBottomItems.Add(item);
        }
    }

    /// <summary>
    /// 两个维度一起判：工程师模式（相机页）、权限位（审计页）。
    /// 分开写两个过滤器只会让"到底哪一条规则挡住的"变得难查。
    /// </summary>
    private bool IsVisible(NavItem item)
    {
        if (item.EngineerOnly && !_isEngineerMode) return false;

        if (item.RequiredPermission != Permission.None
            && !_session.HasPermission(item.RequiredPermission))
        {
            return false;
        }

        return true;
    }

    private void SyncActiveState()
    {
        var route = _navigation.CurrentRoute;

        foreach (var item in _allItems)
            item.IsActive = string.Equals(item.Route, route, StringComparison.Ordinal);

        OnPropertyChanged(nameof(ActivePage));
    }
}

/// <summary>
/// 导航项。
///
/// <see cref="Route"/> 和 <see cref="Label"/> 刻意分开：以前用 Label 当路由 key，
/// 改一次显示文案就会把导航打断（而且中文当 key 时打错一个字是编译期发现不了的）。
/// </summary>
public partial class NavItem : ObservableObject
{
    public NavItem(
        string route,
        string label,
        string geometry,
        bool engineerOnly = false,
        Permission requiredPermission = Permission.None)
    {
        Route = route;
        Label = label;
        Geometry = geometry;
        EngineerOnly = engineerOnly;
        RequiredPermission = requiredPermission;
    }

    /// <summary>路由名，交给 <see cref="INavigationService"/></summary>
    public string Route { get; }

    /// <summary>显示名 / ToolTip</summary>
    public string Label { get; }

    /// <summary>SVG Path 数据</summary>
    public string Geometry { get; }

    /// <summary>
    /// 是否只在工程师模式可见。
    ///
    /// 相机参数、日志这类页面操作员用不到，摆在运行模式的导航里只会让人误点进去，
    /// 看到一堆看不懂的按钮。运行模式和工程师模式的区别，主要就体现在这个标志上。
    /// </summary>
    public bool EngineerOnly { get; }

    /// <summary>
    /// 需要哪个权限位才显示。<see cref="Permission.None"/> = 不需要。
    ///
    /// 与 <see cref="EngineerOnly"/> 是两个维度：相机页看"在不在工程师模式"，
    /// 审计页看"是不是管理员"。后者与模式无关 —— 管理员在运行模式下也该能查审计。
    /// </summary>
    public Permission RequiredPermission { get; }

    /// <summary>当前页是不是我。由 <see cref="SidebarViewModel"/> 单向写入。</summary>
    [ObservableProperty]
    private bool _isActive;
}
