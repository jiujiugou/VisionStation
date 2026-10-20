namespace VisionStation.App.Views;

/// <summary>
/// 检测页视图。构造函数已经归零：五块面板靠 <c>Content</c> 绑定 + App.xaml 的
/// <c>DataTemplate</c> 组装，WPF 自己完成"VM → View"这一步。
/// </summary>
public partial class InspectionPageView
{
    public InspectionPageView() => InitializeComponent();
}
