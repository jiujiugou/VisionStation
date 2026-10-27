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
///   1. 持有导航项（上半部：检测；下半部：图像来源 / 历史 / 审计）
///   2. 点图标 → 请求导航；高亮 = 当前路由的投影，不是控件自己的选中项
///   3. 订阅 TriggerStateMessage → 自动模式暗化、停止时恢复
///
/// 为什么没有“选中项”这个概念了：
///   改造前用两个 <c>ListBox</c> 共享一个 <c>SelectedItem</c>。实测（真 WPF，见
///   docs/WPF规范落地记录.md）两个列表各自记住自己的选中项、互不清除，
///   结果**同时有两个图标是高亮的** —— 点下半部的某一页，上半部的检测还亮着，
///   操作员没法判断"我现在在哪一页"。这不是绑定写错，是"选中"本身不该由控件拥有：
///   导航状态归 <see cref="INavigationService"/>，图标只是把它画出来。
///   所以这里改成按钮 + <see cref="NavItem.IsActive"/>，唯一真相源是 CurrentRoute。
/// </summary>
public partial class SidebarViewModel : ObservableObject
{
    /// <summary>导航服务。**唯一真相源**：本类只读它的 CurrentRoute，从不自己记"当前页"。</summary>
    private readonly INavigationService _navigation;

    /// <summary>会话。只用来查权限位（审计页要不要出现），不读用户名。</summary>
    private readonly ISessionService _session;

    /// <summary>全部导航项（上下两组合并）。<see cref="SyncActiveState"/> 要遍历它把所有项的高亮清掉。</summary>
    private readonly IReadOnlyList<NavItem> _allItems;

    /// <summary>当前是否工程师模式。由 <see cref="EngineerModeMessage"/> 驱动，见 <see cref="ApplyEngineerMode"/>。</summary>
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

    /// <summary>
    /// 构造：建导航项 → 按当前模式与权限算可见集 → 订阅三路消息 → 与导航状态对齐。
    ///
    /// <para>
    /// 顺序不能乱：<see cref="ApplyEngineerMode"/> 里会读 <see cref="_allItems"/>，
    /// 而那是在建完 <see cref="BottomItems"/> 之后才赋值的。
    /// </para>
    /// <para>
    /// <c>TriggerStateMessage</c> 的订阅用的是带接收者的**普通** lambda（不是 static）——
    /// 因为它不需要额外状态，写成 static 只是习惯；真正影响的是这里必须拿 <c>vm</c>，
    /// 所以签名里保留了它。
    /// </para>
    /// </summary>
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
                route: NavigationService.ImageSourceRoute,
                label: "图像来源",
                // 图片框图标：这页管的是"平台存的图在哪"，不是相机硬件
                geometry: "M21 19V5a2 2 0 00-2-2H5a2 2 0 00-2 2v14a2 2 0 002 2h14a2 2 0 002-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z",
                // 相机归视觉平台独占之后，本程序不再有"相机参数"这回事。
                // 工程师真正要调的是图像来源（平台存图目录）—— 那是配置，操作员不该碰。
                engineerOnly: true),
            new NavItem(
                route: NavigationService.RecipeRoute,
                label: "配方",
                // 文档图标：换型时选配方
                geometry: "M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8l-6-6zm-1 7V3.5L18.5 9H13z",
                // 换型是技术员及以上的活：操作员只管启停。
                // 配方选错 = 整批全判错，这类错误不该由按按钮的人承担。
                requiredPermission: Permission.SwitchRecipe),
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
            new NavItem(
                route: NavigationService.UsersRoute,
                label: "用户",
                // 双人图标：账号管理
                geometry: "M16 11c1.66 0 3-1.34 3-3s-1.34-3-3-3-3 1.34-3 3 1.34 3 3 3zm-8 0c1.66 0 3-1.34 3-3S9.66 5 8 5 5 6.34 5 8s1.34 3 3 3zm0 2c-2.33 0-7 1.17-7 3.5V19h14v-2.5C15 14.17 10.33 13 8 13zm8 0c-.29 0-.62.02-.97.05 1.16.84 1.97 1.97 1.97 3.45V19h6v-2.5c0-2.33-4.67-3.5-7-3.5z",
                // 管账号 = 管人，管理员专属：工程师能改参数，但不该能造账号
                requiredPermission: Permission.ManageUsers),
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

    /// <summary>
    /// 导航状态变了就重算高亮。只看两个属性：
    /// CurrentRoute（决定谁亮）与 CurrentPage（页面实例换了但路由名可能没变）。
    /// </summary>
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
    /// 顺带处理一个悬空状态：如果当前正停在即将被隐藏的页上（切回运行模式时人还在「图像来源」页），
    /// 要退回检测页 —— 否则导航栏里没有任何高亮项，内容区却还显示着那一页的配置，
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
        // 退出工程师模式时人可能停在「图像来源」页；登出时人可能停在审计页。
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

    /// <summary>
    /// 按当前模式与权限重建可见项。
    ///
    /// <para>
    /// 用的是"清空 + 重新 Add"而不是原地增删：项数是个位数，重建比维护增删逻辑便宜，
    /// 而且不会出现"删了但没删干净"的半状态。代价是按钮会重建一次 ——
    /// 所以只在模式/权限变化时调，不要放进滚动或每帧路径里。
    /// </para>
    /// </summary>
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
    /// 两个维度一起判：工程师模式（图像来源页）、权限位（审计页）。
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

    /// <summary>
    /// 把所有项的高亮刷新成"当前路由那一项亮"。
    ///
    /// <para>
    /// 遍历全量 <see cref="_allItems"/> 而不是可见集：不可见的项也必须把
    /// <see cref="NavItem.IsActive"/> 清掉，否则切回工程师模式时会看到两个亮项
    /// （隐藏期间它一直保持着 true）。
    /// </para>
    /// </summary>
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
    /// <summary>
    /// 建一个导航项。全部属性只读 —— 导航项在运行时不该被改，模式与权限的
    /// 变化靠"换可见集合"表达，而不是改项的属性。
    /// </summary>
    /// <param name="route">路由名（与 <see cref="NavigationService"/> 的常量一致）</param>
    /// <param name="label">显示文字 / ToolTip</param>
    /// <param name="geometry">SVG Path 数据（Material 风格图标，24×24 视框）</param>
    /// <param name="engineerOnly">是否只在工程师模式可见</param>
    /// <param name="requiredPermission">额外要求的权限位</param>
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
    /// <para>
    /// 图像来源这类页面操作员用不到，摆在运行模式的导航里只会让人误点进去，
    /// 看到一堆看不懂的配置。运行模式和工程师模式的区别，主要就体现在这个标志上。
    /// </para>
    /// </summary>
    public bool EngineerOnly { get; }

    /// <summary>
    /// 需要哪个权限位才显示。<see cref="Permission.None"/> = 不需要。
    ///
    /// 与 <see cref="EngineerOnly"/> 是两个维度：图像来源页看"在不在工程师模式"，
    /// 审计页看"是不是管理员"。后者与模式无关 —— 管理员在运行模式下也该能查审计。
    /// </summary>
    public Permission RequiredPermission { get; }

    /// <summary>当前页是不是我。由 <see cref="SidebarViewModel"/> 单向写入。</summary>
    [ObservableProperty]
    private bool _isActive;
}
