using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using VisionStation.App.Abstractions;
using VisionStation.App.ViewModels;
using VisionStation.App.Views;

namespace VisionStation.App.Services;

/// <summary>
/// <see cref="ILoginPrompt"/> 的 WPF 实现：开一个模态窗口问一次。
///
/// 每次新建 ViewModel 实例，不复用单例 —— 上一次输错的用户名、
/// 残留的错误提示都不该带到下一次登录。
/// </summary>
internal sealed class WpfLoginPrompt : ILoginPrompt
{
    private readonly IServiceProvider _services;

    public WpfLoginPrompt(IServiceProvider services) => _services = services;

    public bool ShowLogin(string? reason = null)
    {
        var viewModel = _services.GetRequiredService<LoginViewModel>();

        if (!string.IsNullOrWhiteSpace(reason))
            viewModel.ErrorText = reason;

        var window = new LoginWindow(viewModel);

        // Owner 必须在窗口 Show 之前设好：
        // 这样登录框居中于主窗、始终压在主窗上方，任务栏里也不会多出一个图标。
        var owner = Application.Current?.MainWindow;
        if (owner is { IsLoaded: true } && !ReferenceEquals(owner, window))
            window.Owner = owner;

        return window.ShowDialog() == true;
    }
}
