using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using VisionStation.App.Services.Navigation;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Tests;

/// <summary>
/// 组合契约：容器里不许出现可视元素。
///
/// 改造前 <c>AddViews</c> 把 8 个 View 和 MainWindow 都注册成单例，
/// 于是"谁释放可视树"这件事落到了 DI 容器头上 —— 而 WPF 的可视元素
/// 有自己的生命周期（模板装载/卸载、线程亲和），容器根本管不了。
/// 这条测试是那类回退的报警器：只要有人再把 View 注册回去就会红。
/// </summary>
public class AppCompositionTests
{
    [Fact]
    public void Container_RegistersNoViewsOrWindows()
    {
        var services = new ServiceCollection();
        services.AddViews();

        var offenders = services
            .Where(d => IsVisualElement(d.ServiceType) || IsInViewsNamespace(d.ServiceType))
            .Select(d => d.ServiceType.FullName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Container_RegistersPagesAndNavigation()
    {
        var services = new ServiceCollection();
        services.AddViews();

        Assert.Contains(services, d => d.ServiceType == typeof(InspectionPageViewModel));
        Assert.Contains(services, d => d.ServiceType == typeof(CameraPageViewModel));
        Assert.Contains(services, d => d.ServiceType == typeof(INavigationService));
        Assert.Contains(services, d => d.ServiceType == typeof(ShellViewModel));
    }

    private static bool IsVisualElement(Type type)
        => typeof(Window).IsAssignableFrom(type) || typeof(UserControl).IsAssignableFrom(type);

    private static bool IsInViewsNamespace(Type type)
        => type.Namespace?.EndsWith(".Views", StringComparison.Ordinal) == true;
}
