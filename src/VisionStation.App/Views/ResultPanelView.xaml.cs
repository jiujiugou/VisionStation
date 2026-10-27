namespace VisionStation.App.Views;

/// <summary>
/// 结果面板视图。**没有 code-behind 逻辑**。
///
/// 判定文字 / 颜色 / 良率全部来自 VM 与 DataTrigger：
/// VM 只给 <c>IsOk</c> 这类语义值，红或绿在 XAML 里决定 —— 换主题不用改代码。
/// </summary>
public partial class ResultPanelView
{
    public ResultPanelView() => InitializeComponent();
}
