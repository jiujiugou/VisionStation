namespace VisionStation.App.Views;

/// <summary>
/// 顶栏视图。**没有 code-behind 逻辑**。
///
/// 模式切换走 <c>TopBarViewModel.ToggleModeCommand</c>，
/// 设备灯靠订阅消息自动刷新 —— 视图不主动去问任何设备"你还在吗"。
/// </summary>
public partial class TopBarView
{
    public TopBarView() => InitializeComponent();
}
