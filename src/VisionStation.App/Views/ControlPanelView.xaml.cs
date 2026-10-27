namespace VisionStation.App.Views;

/// <summary>
/// 控制面板视图。**没有 code-behind 逻辑**。
///
/// 所有按钮都绑 <c>ControlPanelViewModel</c> 的 <c>XxxCommand</c>；
/// 维护区三项的显隐绑 <c>IsEngineerMode</c>（由顶栏广播驱动）。
/// 视图不认识任何设备，按钮点下去发生什么全在 VM 里。
/// </summary>
public partial class ControlPanelView
{
    public ControlPanelView() => InitializeComponent();
}
