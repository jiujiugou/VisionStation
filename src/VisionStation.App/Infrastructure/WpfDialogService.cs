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
    public Task<bool> ConfirmAsync(string title, string message)
        => InvokeAsync(() => MessageBox.Show(
            message, title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes);

    public Task ShowInfoAsync(string title, string message)
        => InvokeAsync(() => MessageBox.Show(
            message, title,
            MessageBoxButton.OK,
            MessageBoxImage.Information));

    public Task ShowErrorAsync(string title, string message)
        => InvokeAsync(() => MessageBox.Show(
            message, title,
            MessageBoxButton.OK,
            MessageBoxImage.Error));

    private static Task<T> InvokeAsync<T>(Func<T> action)
    {
        var dispatcher = CurrentDispatcher();
        return dispatcher.CheckAccess()
            ? Task.FromResult(action())
            : dispatcher.InvokeAsync(action).Task;
    }

    private static Dispatcher CurrentDispatcher()
        => Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
}
