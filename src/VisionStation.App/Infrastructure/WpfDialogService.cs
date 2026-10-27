using System.Windows;
using System.Windows.Threading;
using VisionStation.App.Abstractions;

namespace VisionStation.App.Infrastructure;

/// <summary>
/// WPF 对话框实现。整个 App 只有这里允许出现 MessageBox。
///
/// 线程约定：
///   调用方在 UI 线程时直接弹，避免多余调度；
///   调用方在后台线程时切回 Dispatcher，MessageBox 不跨线程碰 WPF。
/// </summary>
public sealed class WpfDialogService : IDialogService
{
    /// <summary>问"要不要继续"。默认按钮是"是"，图标用警告黄 —— 这类确认多用于不可逆操作。</summary>
    public Task<bool> ConfirmAsync(string title, string message)
        => InvokeAsync(() => MessageBox.Show(
            message, title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes);

    /// <summary>纯提示，用户只能点确定。</summary>
    public Task ShowInfoAsync(string title, string message)
        => InvokeAsync(() => MessageBox.Show(
            message, title,
            MessageBoxButton.OK,
            MessageBoxImage.Information));

    /// <summary>
    /// 错误提示。注意：**启动自检失败不要走这里** ——
    /// 模态框会阻塞启动线程，而自检失败的正确处理是"记日志 + 状态栏标红 + 继续启动"。
    /// 这里只用于"用户主动操作失败，必须让他知道"的场合。
    /// </summary>
    public Task ShowErrorAsync(string title, string message)
        => InvokeAsync(() => MessageBox.Show(
            message, title,
            MessageBoxButton.OK,
            MessageBoxImage.Error));

    /// <summary>
    /// 保证 action 在 UI 线程执行，并把同步的 MessageBox 包成 Task。
    /// <para>
    /// 注意 <c>InvokeAsync(...).Task</c> 只在 UI 线程被占用时才是"异步"的 ——
    /// MessageBox 本身是模态的，它会阻塞 UI 线程直到用户点掉。
    /// 这是 Win32 MessageBox 的固有行为，不是这里的实现取巧。
    /// </para>
    /// </summary>
    private static Task<T> InvokeAsync<T>(Func<T> action)
    {
        var dispatcher = CurrentDispatcher();
        return dispatcher.CheckAccess()
            ? Task.FromResult(action())
            : dispatcher.InvokeAsync(action).Task;
    }

    /// <summary>
    /// 取"当前该用的" Dispatcher。
    /// <para>
    /// 没有 Application（单测环境直接 new 这个服务）时退到 <see cref="Dispatcher.CurrentDispatcher"/>：
    /// 它会为当前线程造一个新 dispatcher，CheckAccess 返回 true，于是走同步分支。
    /// 换句话说，单测里弹框不会卡住，也不会抛"调用线程无法访问此对象"。
    /// </para>
    /// </summary>
    private static Dispatcher CurrentDispatcher()
        => Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
}
