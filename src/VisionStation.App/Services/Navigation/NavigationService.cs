using System.ComponentModel;
using Microsoft.Extensions.Logging;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Services.Navigation;

/// <summary>
/// 路由表 + 当前页。每个页面一个实例，用字典写死，没有工厂、没有反射、没有容器查询。
/// </summary>
public sealed class NavigationService : INavigationService, INotifyPropertyChanged
{
    public const string InspectionRoute = "检测";
    public const string ImageSourceRoute = "图像来源";
    public const string HistoryRoute = "历史";
    public const string AuditRoute = "审计";

    private readonly Dictionary<string, object> _pages;
    private readonly ILogger<NavigationService> _logger;

    private object? _currentPage;
    private string _currentRoute = InspectionRoute;

    public NavigationService(
        InspectionPageViewModel inspectionPage,
        ImageSourcePageViewModel imageSourcePage,
        HistoryPageViewModel historyPage,
        AuditPageViewModel auditPage,
        ILogger<NavigationService> logger)
    {
        _logger = logger;

        _pages = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [InspectionRoute] = inspectionPage,
            [ImageSourceRoute] = imageSourcePage,
            [HistoryRoute] = historyPage,
            [AuditRoute] = auditPage,
        };

        // 启动即停在检测页：和改造前一致（窗口打开看到的就是检测页）。
        _currentPage = inspectionPage;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public object? CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (ReferenceEquals(_currentPage, value)) return;
            _currentPage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentPage)));
        }
    }

    public string CurrentRoute
    {
        get => _currentRoute;
        private set
        {
            if (_currentRoute == value) return;
            _currentRoute = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentRoute)));
        }
    }

    public bool Navigate(string route)
    {
        if (!_pages.TryGetValue(route, out var page))
        {
            // 参数/配方/历史/PLC/日志/系统这些页面还没做。返回 false 让调用方
            // 保持原选中项，而不是把一个空白页当成"已实现"。
            _logger.LogWarning("导航到未实现的路由: {Route}", route);
            return false;
        }

        CurrentPage = page;
        CurrentRoute = route;
        return true;
    }
}
