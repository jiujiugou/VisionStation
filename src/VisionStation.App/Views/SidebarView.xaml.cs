namespace VisionStation.App.Views;

/// <summary>
/// 侧边栏视图。**没有 code-behind 逻辑**。
///
/// 导航 = 按钮命令，高亮 = <c>NavItem.IsActive</c>（由 VM 从当前路由单向同步）。
/// 视图不持有"当前选中谁"这个状态 —— 那是导航服务的职责。
/// </summary>
public partial class SidebarView
{
    public SidebarView() => InitializeComponent();
}
