namespace VisionStation.App.Views;

/// <summary>
/// 日志面板视图。**没有 code-behind 逻辑**。
///
/// 绑定 <c>VisibleEntries</c>（已按级别过滤）、行颜色靠转换器、
/// 过滤按钮绑 <c>ToggleFilterCommand</c>。合批刷新全在 VM 里做，
/// 视图这边不做任何"定时拉一次"的事。
/// </summary>
public partial class LogPanelView
{
    public LogPanelView() => InitializeComponent();
}
