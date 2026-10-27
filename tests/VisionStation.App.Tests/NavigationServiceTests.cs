using Microsoft.Extensions.Logging.Abstractions;
using VisionStation.App.Services.Navigation;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Tests;

/// <summary>
/// 导航契约。
///
/// 改造前"导航"是发一条 <c>NavigateMessage</c>，窗口收到后改两个 Visibility ——
/// 没人能回答"当前页是谁"，也没有任何东西能被测试。现在它是一个状态：
/// 两个页面 + 一个路由名，可断言、可校验未知路由。
/// </summary>
public class NavigationServiceTests
{
    [Fact]
    public void Constructor_StartsOnInspectionPage()
    {
        var (service, inspection, _) = CreateService();

        Assert.Same(inspection, service.CurrentPage);
        Assert.Equal(NavigationService.InspectionRoute, service.CurrentRoute);
    }

    [Fact]
    public void Navigate_KnownRoute_SwitchesPageAndRaisesChange()
    {
        var (service, _, camera) = CreateService();
        var changed = new List<string?>();
        service.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(service.Navigate(NavigationService.ImageSourceRoute));

        Assert.Same(camera, service.CurrentPage);
        Assert.Equal(NavigationService.ImageSourceRoute, service.CurrentRoute);
        Assert.Contains(nameof(NavigationService.CurrentPage), changed);
        Assert.Contains(nameof(NavigationService.CurrentRoute), changed);
    }

    [Fact]
    public void Navigate_UnknownRoute_ReturnsFalseAndKeepsCurrentPage()
    {
        var (service, inspection, _) = CreateService();

        // 参数/配方/历史/PLC/日志/系统都还没实现：必须明确失败，
        // 不能悄悄把当前页换成一个空白页（操作员会以为软件坏了）
        Assert.False(service.Navigate("参数"));
        Assert.False(service.Navigate(""));

        Assert.Same(inspection, service.CurrentPage);
        Assert.Equal(NavigationService.InspectionRoute, service.CurrentRoute);
    }

    [Fact]
    public void Navigate_SamePageTwice_KeepsInstanceAndDoesNotRaiseDuplicateChange()
    {
        var (service, inspection, _) = CreateService();
        service.Navigate(NavigationService.ImageSourceRoute);

        int pageChanges = 0;
        service.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NavigationService.CurrentPage)) pageChanges++;
        };

        Assert.True(service.Navigate(NavigationService.ImageSourceRoute));

        // 已在图像来源页时重复导航不应触发绑定更新（整页重建 = 面板状态丢失）
        Assert.Equal(0, pageChanges);
        Assert.Same(inspection, service.Navigate(NavigationService.InspectionRoute) ? service.CurrentPage : null);
    }

    private static (NavigationService Service, InspectionPageViewModel Inspection, ImageSourcePageViewModel Camera) CreateService()
    {
        var inspection = TestPages.Inspection();
        var camera = TestPages.ImageSource();
        var service = new NavigationService(inspection, camera, TestPages.History(),
            new AuditPageViewModel(new FakeAuditService()), NullLogger<NavigationService>.Instance);
        return (service, inspection, camera);
    }
}
