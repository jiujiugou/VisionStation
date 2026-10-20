namespace VisionStation.App.Views;

/// <summary>
/// 侧边栏视图。**没有 code-behind 逻辑**：导航就是"列表选中了谁"，
/// 选中项 TwoWay 绑到 <c>SelectedNavItem</c>，点未实现的页面时 VM 会把选中项退回去。
/// </summary>
public partial class SidebarView
{
    public SidebarView() => InitializeComponent();
}
