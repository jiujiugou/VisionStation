namespace VisionStation.App.Composition;

/// <summary>
/// 窗口关闭请求参数。
///
/// 为什么不直接用 WPF 的 <see cref="System.ComponentModel.CancelEventArgs"/>：
/// App.Tests 里的窗口替身不该被迫引用 WPF 事件类型，而"关闭被接管"这件事
/// 只需要一个可写的 Cancel 标志。真实窗口负责把这个对象翻译回 WPF 的 CancelEventArgs。
/// </summary>
public sealed class WindowCloseRequestedEventArgs : EventArgs
{
    /// <summary>置 true = 本次关闭被接管（窗口先不关，由收尾流程决定何时退出）。</summary>
    public bool Cancel { get; set; }
}

/// <summary>
/// 宿主（<c>AppHost</c>）眼里的"一个窗口"。存在的意义有两个：
///   1. 让宿主不直接依赖 WPF 的 <see cref="System.Windows.Window"/>，App.Tests 能用假窗口驱动启停；
///   2. 把"关闭可以被接管"这件事表达成显式契约，而不是靠事件参数的类型耦合。
/// </summary>
public interface IAppWindow
{
    /// <summary>
    /// 显示窗口。调用方保证只调一次（宿主自己用 <c>_window != null</c> 兜住重复调用）。
    /// 实现应当是非阻塞的：WPF 的 <c>Show()</c> 立即返回，真正的消息循环由 Application.Run 驱动。
    /// </summary>
    void Show();

    /// <summary>
    /// 关闭请求。处理器可以设置 <see cref="WindowCloseRequestedEventArgs.Cancel"/> 拦截本次关闭。
    /// 契约（设计文档 C6）：同一轮关闭只允许被接管一次，重复请求必须被忽略。
    /// </summary>
    event EventHandler<WindowCloseRequestedEventArgs>? Closing;
}
