namespace VisionStation.App.Views;

/// <summary>
/// 状态栏视图。**没有 code-behind 逻辑**。
///
/// 左侧"平台 / PLC / 节拍"三个指示灯全部靠消息驱动刷新，
/// 视图不轮询、不做定时器 —— 那会让一个纯显示控件变成第三个数据源。
/// </summary>
public partial class StatusBarView
{
    public StatusBarView() => InitializeComponent();
}
