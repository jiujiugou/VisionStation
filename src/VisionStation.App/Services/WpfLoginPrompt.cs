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
    /// <summary>只用来解析 <see cref="LoginViewModel"/>；窗口本身不归容器管。</summary>
    private readonly IServiceProvider _services;

    public WpfLoginPrompt(IServiceProvider services) => _services = services;

    /// <summary>
    /// 同步阻塞地弹登录窗（ShowDialog），返回是否登录成功。
    ///
    /// <para>
    /// 调用方一定在 UI 线程（它由用户点按钮/切模式触发），所以这里不做 Dispatcher 调度。
    /// 如果将来有后台线程想调它，必须先 marshal 回 UI 线程 —— ShowDialog 在非 UI 线程
    /// 会抛 InvalidOperationException，而且错误信息不会提到"线程"。
    /// </para>
    /// </summary>
    public bool ShowLogin(string? reason = null)
    {
        var viewModel = _services.GetRequiredService<LoginViewModel>();

        // 预置错误文案：用于"刚因权限不足被拦下来"这种场景，
        // 让用户一眼看到为什么要再登一次，而不是一个没有上下文的空登录框。
        if (!string.IsNullOrWhiteSpace(reason))
            viewModel.ErrorText = reason;

        var window = new LoginWindow(viewModel);

        // Owner 必须在窗口 Show 之前设好：
        // 这样登录框居中于主窗、始终压在主窗上方，任务栏里也不会多出一个图标。
        // 两个前提检查：主窗已加载（IsLoaded）才能当 Owner，否则 WPF 会抛；
        // 且不能把自己设成自己的 Owner。
        var owner = Application.Current?.MainWindow;
        if (owner is { IsLoaded: true } && !ReferenceEquals(owner, window))
            window.Owner = owner;

        return window.ShowDialog() == true;
    }
}
