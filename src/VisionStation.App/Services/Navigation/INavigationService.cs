using System.ComponentModel;

namespace VisionStation.App.Services.Navigation;

/// <summary>
/// 页面导航。Shell 只认得这一个入口。
///
/// 为什么不是消息：改造前 <c>SidebarViewModel</c> 发 <c>NavigateMessage</c>，
/// <c>MainWindow</c> 收到后改 <c>Visibility</c>。那条链路的实际含义是
/// "侧边栏通知窗口换一个显示层"，不是"当前页变成了谁"——
/// 于是窗口必须知道所有页面、所有页面必须常驻在同一层 Grid 里，
/// 加第三页就得再改一次窗口。导航是状态，不是通知。
///
/// 页面实例由本服务持有（构造注入），路由表写死在构造函数里：
/// 目前两个页面都是常驻工作台（不是"打开某条记录"式的多实例页面），
/// 所以不引入工厂、不引入 <c>IServiceProvider</c>。真出现多实例页面再谈。
/// </summary>
/// <remarks>继承 <see cref="INotifyPropertyChanged"/>：外壳要绑定 CurrentPage，
/// 导航变化必须能被观察，而不是靠再发一条消息。</remarks>
public interface INavigationService : INotifyPropertyChanged
{
    /// <summary>当前页面 VM。首次启动即检测页。</summary>
    object? CurrentPage { get; }

    /// <summary>当前路由名，和侧边栏的 Label 一致。</summary>
    string CurrentRoute { get; }

    /// <summary>
    /// 切换页面。未知路由返回 false 并留日志，**不会**静默什么都不做，
    /// 也不会把当前页换成一个空页（那会让操作员以为软件坏了）。
    /// </summary>
    bool Navigate(string route);
}
