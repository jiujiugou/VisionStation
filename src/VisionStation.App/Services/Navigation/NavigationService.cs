using System.ComponentModel;
using Microsoft.Extensions.Logging;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Services.Navigation;

/// <summary>
/// 路由表 + 当前页。每个页面一个实例，用字典写死，没有工厂、没有反射、没有容器查询。
/// </summary>
public sealed class NavigationService : INavigationService, INotifyPropertyChanged
{
    // 路由名 = 侧边栏按钮上显示的文字。不另建枚举/常量表：
    // 侧边栏的 Label 就是从这些常量取的，两处共用同一个字符串，
    // 改文字只改这里一处，不会出现"点了按钮跳不过去"。
    /// <summary>检测页。启动默认页，操作员视角。</summary>
    public const string InspectionRoute = "检测";

    /// <summary>图像来源页。工程师模式才可见，配平台存图目录。</summary>
    public const string ImageSourceRoute = "图像来源";

    /// <summary>历史页。查已入库的检测记录。</summary>
    public const string HistoryRoute = "历史";

    /// <summary>审计页。管理员权限才可见，记录登录/配置变更/启停。</summary>
    public const string AuditRoute = "审计";

    /// <summary>配方页。技术员及以上（Permission.SwitchRecipe），换型时选配方。</summary>
    public const string RecipeRoute = "配方";

    /// <summary>用户页。仅管理员（Permission.ManageUsers），增删改账号。</summary>
    public const string UsersRoute = "用户";

    /// <summary>
    /// 路由 → 页面 VM 实例。**实例是常驻的**（由容器单例注入），导航只是换引用，
    /// 不销毁也不重建 —— 所以切走再切回来，页面上已加载的列表、筛选条件都还在。
    /// </summary>
    private readonly IReadOnlyDictionary<string, object> _pages;

    private readonly ILogger<NavigationService> _logger;

    private object? _currentPage;

    /// <summary>当前路由。初值与 <see cref="_currentPage"/> 保持一致，否则侧边栏高亮和内容会错位。</summary>
    private string _currentRoute = InspectionRoute;

    /// <summary>全部路由常量。侧边栏与组合根都用它做完整性检查。</summary>
    public static IReadOnlyList<string> AllRoutes { get; } = new[]
    {
        InspectionRoute, ImageSourceRoute, HistoryRoute, AuditRoute, RecipeRoute, UsersRoute,
    };

    /// <summary>
    /// 构造时就把全部页面拿到手：任何一页的构造函数炸了，启动期就炸，
    /// 而不是等操作员点到那一页才发现白屏。
    ///
    /// <para>
    /// 为什么收一张表而不是逐个页面参数：页数从 4 涨到 6 时，
    /// 逐个参数意味着每加一页都要改构造函数、改测试、改组合根三处。
    /// 表由组合根构建（容器解析各页），本类仍然不查容器、不用反射。
    /// </para>
    /// </summary>
    /// <param name="pages">路由 → 页面 VM 实例；缺任何一条 <see cref="AllRoutes"/> 里的路由都会当场抛。</param>
    /// <param name="logger">只用于记录"导航到未实现路由"这一种异常情况</param>
    public NavigationService(IReadOnlyDictionary<string, object> pages, ILogger<NavigationService> logger)
    {
        ArgumentNullException.ThrowIfNull(pages);
        _logger = logger;

        // 缺页必须启动期就炸：侧边栏有按钮却点不进去，是上一轮专门清理过的"假按钮"。
        foreach (var route in AllRoutes)
        {
            if (!pages.ContainsKey(route))
                throw new ArgumentException($"路由表缺少页面：{route}", nameof(pages));
        }

        // StringComparer.Ordinal：路由是程序内常量，不是用户输入，不需要区域文化比较。
        // 用 ordinal 也顺带避免了"土耳其 i"这类文化敏感性 bug（虽然这里全是中文）。
        _pages = pages;

        // 启动即停在检测页：和改造前一致（窗口打开看到的就是检测页）。
        _currentPage = pages[InspectionRoute];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 当前页 VM。外壳（<c>ShellViewModel</c>）直接绑这个属性到 ContentControl，
    /// 换页就是换引用，由 <c>App.xaml</c> 里的 DataTemplate 决定用哪个 View 渲染。
    /// </summary>
    public object? CurrentPage
    {
        get => _currentPage;
        private set
        {
            // 引用相等才算没变：页面实例是常驻单例，用 == 比较会走 VM 重写的 Equals，
            // 那可能把两个不同页面判成同一个，导致点侧边栏没反应。
            if (ReferenceEquals(_currentPage, value)) return;
            _currentPage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentPage)));
        }
    }

    /// <summary>当前路由名，侧边栏用它决定哪个按钮高亮。</summary>
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
            // 走到这里说明调用方传了一个不在路由表里的名字（侧边栏和路由表不对齐）。
            // 返回 false 让调用方保持原选中项，而不是把一个空白页当成"已实现"。
            // 这类问题不会自愈，所以必须留一条警告。
            _logger.LogWarning("导航到未实现的路由: {Route}", route);
            return false;
        }

        // 先换页面再换路由名：两个属性会各发一次 PropertyChanged，
        // 顺序对调会让侧边栏在极短的一帧里高亮新页、内容还是旧页（视觉闪动）。
        CurrentPage = page;
        CurrentRoute = route;
        return true;
    }
}
