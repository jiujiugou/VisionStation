using System.IO;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionStation.App.Abstractions;
using VisionStation.App.Services;
using VisionStation.App.Composition;
using VisionStation.App.Composition.Lifecycle;
using VisionStation.App.Services.Imaging;
using VisionStation.App.Services.Inspection;
using VisionStation.App.Services.Navigation;
using VisionStation.App.ViewModels;
using VisionStation.Core.Models;
using VisionStation.Core.Engines;
using VisionStation.Core.Users;
using VisionStation.Devices;
using VisionStation.Service.Security;
using VisionStation.Storage;
using VisionStation.Service.Audit;
using VisionStation.Service.Recipes;
using VisionStation.Storage.Entities;

namespace VisionStation.App.Tests;

// S2.3 关闭流程的共用替身。
// 刻意不引入 Moq：这些接口都很窄，手写替身能一眼看出"到底被调了几次"，
// 也不必为了一个 bool 去背表达式树的语法。

/// <summary>触发源替身：可手动 Fire，记录 Stop 次数。</summary>
internal sealed class FakeTriggerSource : ITriggerSource
{
    public event Action<TriggerOrigin>? Triggered;

    public bool IsRunning { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public int FireCount { get; private set; }

    /// <summary>下一次触发要带的来源。默认手动；测"定时触发要记成 Timer"时改成 Timer。</summary>
    public TriggerOrigin NextOrigin { get; set; } = TriggerOrigin.Manual;

    public void Start()
    {
        StartCount++;
        IsRunning = true;
    }

    public void Stop()
    {
        StopCount++;
        IsRunning = false;
    }

    public void Fire()
    {
        FireCount++;
        Triggered?.Invoke(NextOrigin);
    }

    public void Dispose() { }
}

/// <summary>仓储替身：关闭流程不关心数据库内容，只保证接口齐。</summary>
internal sealed class FakeInspectionRepository : IInspectionRepository
{
    public int SaveCount { get; private set; }

    /// <summary>
    /// 每次入库的 <see cref="InspectionReport"/>（引用，不是副本）。
    ///
    /// 记引用而不是深拷贝：工作流会**先读图、再回写 report.ImagePath、最后入库**，
    /// 断言"库里那条记录的路径指向实际显示的那张图"要看到的就是回写后的值。
    /// 若记副本，考的是"入库那一刻的快照"，反而测不到回写。
    /// </summary>
    public List<InspectionReport> SavedReports { get; } = new();

    /// <summary>最近一次入库的报告；一次都没入库时为 null。</summary>
    public InspectionReport? LastSaved => SavedReports.Count == 0 ? null : SavedReports[^1];

    public Task EnsureCreatedAsync() => Task.CompletedTask;

    public Task SaveAsync(InspectionReport report)
    {
        SaveCount++;
        SavedReports.Add(report);
        return Task.CompletedTask;
    }

    public Task<(int OkCount, int NgCount)> GetStatisticsAsync(DateTime? since = null) =>
        Task.FromResult((0, 0));

    public Task<int> DeleteOlderThanAsync(DateTime cutoff) => Task.FromResult(0);

    public Task<List<InspectionRecord>> QueryAsync(InspectionQuery query) =>
        Task.FromResult(new List<InspectionRecord>());

    public Task<int> CountAsync(InspectionQuery query) => Task.FromResult(0);

    public Task<List<string>> ListEngineNamesAsync() => Task.FromResult(new List<string>());
}

/// <summary>
/// 会**记住查询条件**的仓储替身，给历史页这类"只负责把条件传下去"的 VM 用。
///
/// <para>
/// 为什么不用 <see cref="FakeInspectionRepository"/>：那个替身永远返回空列表、也不记条件，
/// 适合"只关心入了几次库"的用例。历史页要验的恰恰是"用户输的条码有没有真的变成查询条件" ——
/// 这只有把条件留下来才能断言。
/// </para>
/// <para>
/// 返回的 <see cref="Rows"/> 由用例自己填：不填就是空库，正好测"查不到"那几条分支。
/// </para>
/// </summary>
internal sealed class RecordingInspectionRepository : IInspectionRepository
{
    /// <summary>最近一次 QueryAsync 的条件；一次都没查过时为 null。</summary>
    public InspectionQuery? LastQuery { get; private set; }

    /// <summary>最近一次 CountAsync 的条件。与 <see cref="LastQuery"/> 分开记，用来验"两次查询用同一套条件"。</summary>
    public InspectionQuery? LastCountQuery { get; private set; }

    /// <summary>查询要返回的记录（用例自己填）。</summary>
    public List<InspectionRecord> Rows { get; } = new();

    public Task EnsureCreatedAsync() => Task.CompletedTask;

    public Task SaveAsync(InspectionReport report) => Task.CompletedTask;

    public Task<(int OkCount, int NgCount)> GetStatisticsAsync(DateTime? since = null) =>
        Task.FromResult((0, 0));

    public Task<int> DeleteOlderThanAsync(DateTime cutoff) => Task.FromResult(0);

    public Task<List<InspectionRecord>> QueryAsync(InspectionQuery query)
    {
        LastQuery = query;
        // 这里不做筛选：验的是"条件传下去了没"，筛选逻辑在仓储实现里另有测试。
        return Task.FromResult(Rows.ToList());
    }

    public Task<int> CountAsync(InspectionQuery query)
    {
        LastCountQuery = query;
        return Task.FromResult(Rows.Count);
    }

    public Task<List<string>> ListEngineNamesAsync() => Task.FromResult(new List<string>());
}

/// <summary>可观察的 IDisposable：记录是否被释放、在哪条线程上释放。</summary>
internal sealed class FakeDisposable : IDisposable
{
    private readonly Action _onDispose;

    public FakeDisposable(Action onDispose) => _onDispose = onDispose;

    public void Dispose() => _onDispose();
}

/// <summary>对话框替身：S3a/S3b 的页面组合不关心弹窗，只要求接口齐。</summary>
internal sealed class NoopDialog : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

    public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;

    public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
}

/// <summary>配方存储替身：页面组合不写盘。</summary>
internal sealed class NoopRecipeStore : IRecipeStore
{
    public string Folder => System.IO.Path.GetTempPath();

    public string? ActiveName => "test.json";

    public Task<string> SaveAsync(RecipeConfig recipe, CancellationToken ct = default)
        => Task.FromResult(@"C:/recipes/test.json");

    public Task<IReadOnlyList<RecipeSummary>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecipeSummary>>(Array.Empty<RecipeSummary>());

    public Task<RecipeConfig?> LoadAsync(string fileName, CancellationToken ct = default)
        => Task.FromResult<RecipeConfig?>(null);

    public Task SetActiveAsync(string fileName, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>
/// 检测工作流替身。
///
/// 为什么要它：页面 VM 的两条并发契约 —— "两个触发只跑一帧"和"关闭时等在途" ——
/// 本质都是"这一帧什么时候返回"。真工作流里采图/检测/存库全是同步阻塞的，
/// 没有可控的让出点，只能靠替身把"返回时机"拿到测试手里。
/// </summary>
internal sealed class FakeWorkflow : IInspectionWorkflow
{
    /// <summary>非空时，RunOnceAsync 挂在这个 TCS 上，由测试决定何时返回。</summary>
    public TaskCompletionSource<InspectionRunResult?>? Gate { get; init; }

    /// <summary>立即返回的结果（Gate 为空时使用）。</summary>
    public InspectionRunResult? Result { get; set; }

    public int RunCount { get; private set; }

    public CancellationToken LastToken { get; private set; }

    /// <summary>最近一次触发带进来的来源，用来断言"定时触发确实被记成 Timer"。</summary>
    public TriggerOrigin LastOrigin { get; private set; }

    /// <summary>VM 推送进来的报告（按到达顺序记录），用来断言"外部结果确实走到了工作流"。</summary>
    public List<InspectionReport> ExternalReports { get; } = new();

    public async Task<InspectionRunResult?> RunOnceAsync(TriggerOrigin origin, CancellationToken ct)
    {
        RunCount++;
        LastToken = ct;
        LastOrigin = origin;

        if (Gate is { } gate) return await gate.Task.WaitAsync(ct).ConfigureAwait(false);

        return Result;
    }

    public Task<InspectionRunResult?> AcceptExternalAsync(InspectionReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        ExternalReports.Add(report);
        LastToken = ct;

        return Task.FromResult(Result);
    }
}

/// <summary>
/// 检测引擎替身。
///
/// 与 <see cref="FakeWorkflow"/> 的分工：那个替身替掉"整条链路"（含采图/存图/存库），
/// 这个只替掉"判定" —— 工作流自己的职责（尺寸校验、存图失败计数、帧释放）
/// 必须用真工作流传真引擎跑，才测得到。
/// </summary>
internal sealed class FakeEngine : IInspectionEngine
{
    /// <summary>固定返回的报告（未设置 Factory 时使用）</summary>
    public InspectionReport? Report { get; set; }

    /// <summary>按请求生成报告（优先级高于 Report）</summary>
    public Func<InspectionRequest, InspectionReport>? Factory { get; set; }

    public int InspectCount { get; private set; }

    public string Name => "FakeEngine";

    public InspectionReport Inspect(InspectionRequest request)
    {
        InspectCount++;
        return Factory?.Invoke(request)
            ?? Report
            ?? InspectionReport.Failure(Name, "替身未配置报告", request.TriggerSource, request.ImagePath);
    }

    public void Dispose() { }
}

/// <summary>测试用的配方：数据落临时目录的东西一律不碰真实配置。</summary>
internal static class TestPaths
{
    /// <summary>带一个模块声明的配方 —— 贴近产线实际形状（输出映射非空）</summary>
    public static RecipeConfig Recipe()
    {
        var recipe = new RecipeConfig { Name = "测试配方", Engine = EngineNames.Vm };
        recipe.Vm.Outputs.Add(new VmOutputMapping
        {
            Module = "圆查找1",
            Label = "外径",
            Role = ResultKeys.Measure
        });
        return recipe;
    }
}

/// <summary>造一帧"可显示"的工作流结果：真报告 + 已冻结的 1x1 位图。</summary>
internal static class TestResults
{
    public static InspectionRunResult Frame(bool ok = true, params string[] toolNames)
    {
        var report = Report(ok, toolNames);
        return new InspectionRunResult(
            report,
            Bitmap(),
            Bitmap(),
            CycleMs: 12,
            ImageSaved: true,
            ImageError: null,
            ConsecutiveImageFailures: 0,
            ImageLinkOnline: true);
    }

    public static InspectionReport Report(bool ok = true, params string[] toolNames)
    {
        var names = toolNames.Length == 0 ? new[] { "Presence" } : toolNames;
        var report = new InspectionReport();
        foreach (var name in names)
        {
            report.Results[name] = ok
                ? ToolResult.Ok($"{name} 摘要")
                : new ToolResult { IsOk = false, Summary = $"{name} 超差" };
        }

        return report;
    }

    public static BitmapSource Bitmap()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24, null, new byte[3], 3);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>页面 VM 的组装助手：测试里只造"能构造出来"的对象，不碰 WPF。</summary>
internal static class TestPages
{
    /// <summary>
    /// 造一个检测页。同步上下文传 null = "没有 UI 线程"，触发就地执行，
    /// 所以断言可以在 Fire() 返回之后立刻做（工作流替身返回已完成的任务）。
    /// </summary>
    public static InspectionPageViewModel Inspection(
        IInspectionWorkflow? workflow = null,
        FakeTriggerSource? trigger = null,
        ShutdownState? shutdown = null,
        InFlightWorkMonitor? inFlight = null,
        ILogger<InspectionPageViewModel>? logger = null,
        IInspectionEngine? engine = null)
    {
        trigger ??= new FakeTriggerSource();

        return new InspectionPageViewModel(
            new ImagePanelViewModel(),
            new ResultPanelViewModel(),
            new LogPanelViewModel(),
            new ControlPanelViewModel(trigger, new NoopDialog(),
                engine ?? new FakeEngine(), TestPaths.Recipe(), new FakeAuditService()),
            workflow ?? new FakeWorkflow(),
            trigger,
            shutdown ?? new ShutdownState(),
            inFlight ?? new InFlightWorkMonitor(),
            logger ?? NullLogger<InspectionPageViewModel>.Instance);
    }

    /// <summary>历史页：只需要一个仓储替身（不查真库）。</summary>
    public static HistoryPageViewModel History(IInspectionRepository? repo = null)
        => new(repo ?? new FakeInspectionRepository());

    /// <summary>
    /// 图像来源页：设置文件指到临时目录，测试之间互不干扰、也不碰真实设置。
    /// 审计替身可传入 —— 要断言"保存设置留下了 ParameterChange 流水"的用例需要拿到同一个实例。
    /// </summary>
    public static ImageSourcePageViewModel ImageSource(IAuditService? audit = null)
    {
        var path = Path.Combine(Path.GetTempPath(),
            "vs-imsrc-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        return new ImageSourcePageViewModel(new ImageSourceSettingsStore(path),
            audit ?? new FakeAuditService());
    }
}

/// <summary>
/// 导航替身：记录被请求的路由，路由表由测试自己填。
/// 未登记的路由返回 false —— 和真实实现同样的契约（不静默换空页）。
/// </summary>
internal sealed class FakeNavigationService : INavigationService
{
    private readonly Dictionary<string, object> _pages = new(StringComparer.Ordinal);

    public FakeNavigationService(string initialRoute, object initialPage)
    {
        CurrentRoute = initialRoute;
        CurrentPage = initialPage;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public object? CurrentPage { get; private set; }

    public string CurrentRoute { get; private set; }

    public List<string> Attempts { get; } = new();

    public FakeNavigationService With(string route, object page)
    {
        _pages[route] = page;
        return this;
    }

    public bool Navigate(string route)
    {
        Attempts.Add(route);

        if (!_pages.TryGetValue(route, out var page)) return false;

        if (!ReferenceEquals(CurrentPage, page))
        {
            CurrentPage = page;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentPage)));
        }

        if (CurrentRoute != route)
        {
            CurrentRoute = route;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentRoute)));
        }

        return true;
    }
}

/// <summary>
/// 日志替身：把每条日志（含格式化后的文本）收进列表，供断言"这件事有没有被说出来"。
///
/// 存在的理由：本项目的多处契约不是"返回值对不对"，而是"出问题时有没有留下可归因的痕迹"。
/// 丢帧、存图连续失败、配方不匹配都属于这一类 —— 用 NullLogger 就永远测不到。
/// </summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>
/// 审计替身：只把调用收进列表，不写库、不起后台线程。
///
/// 需要它的理由：埋点直接写在命令里，而"按一下按钮有没有留痕"这种断言
/// 不该依赖 SQLite —— 那会把一条命令测试变成数据库集成测试，跑得慢还更脆。
/// </summary>
internal sealed class FakeAuditService : IAuditService
{
    public List<(AuditAction Action, string Target, string? Before, string? After, AuditResult Result, string? Detail)>
        Entries { get; } = new();

    /// <summary>指定身份那一路的记录（登录/登出用的是它）</summary>
    public List<(string User, string Role, AuditAction Action, AuditResult Result)> RecordedAs { get; } = new();

    public void Record(AuditAction action, string target = "", string? before = null, string? after = null,
        AuditResult result = AuditResult.Success, string? detail = null)
        => Entries.Add((action, target, before, after, result, detail));

    public void RecordAs(string userName, string userRole, AuditAction action, string target = "",
        string? before = null, string? after = null,
        AuditResult result = AuditResult.Success, string? detail = null)
    {
        RecordedAs.Add((userName, userRole, action, result));
        Entries.Add((action, target, before, after, result, detail));
    }

    public Task<IReadOnlyList<AuditEntry>> QueryRecentAsync(int count = 200)
        => Task.FromResult<IReadOnlyList<AuditEntry>>(Array.Empty<AuditEntry>());

    /// <summary>每次查询收到的参数，供断言"过滤条件确实传下去了"</summary>
    public List<(AuditAction? Action, string? User, DateTime? Since, int Count)> Queries { get; } = new();

    /// <summary>下一次查询返回什么</summary>
    public List<AuditEntry> QueryResult { get; } = new();

    /// <summary>非 null 时查询抛这个异常，用于验"查询失败要说出来"</summary>
    public Exception? QueryException { get; set; }

    public Task<IReadOnlyList<AuditEntry>> QueryAsync(
        AuditAction? action = null, string? userName = null, DateTime? since = null, int count = 200)
    {
        Queries.Add((action, userName, since, count));

        if (QueryException is not null)
            throw QueryException;

        return Task.FromResult<IReadOnlyList<AuditEntry>>(QueryResult.ToList());
    }

    public void Flush(TimeSpan timeout)
    {
        // 没有队列，无需收尾
    }
}

/// <summary>
/// 会话替身。
///
/// 直接给定角色而不是走一遍真实认证 —— 这些测试要验的是"没权限时界面怎么反应"，
/// 不是"密码对不对"（那是 UnitTests 里 SecurityTests 的事）。
/// </summary>
internal sealed class FakeSessionService : ISessionService
{
    private UserAccount? _user;
    private UserRole _effectiveRole = UserRole.Operator;

    public UserAccount? CurrentUser => _user;

    public UserRole EffectiveRole => _user is null ? UserRole.Operator : _effectiveRole;

    public bool IsAuthenticated => _user is not null;

    public event EventHandler<SessionChangedEventArgs>? Changed;

    /// <summary>模拟"已登录某人"，并像真实会话一样广播一次变化</summary>
    public void SignIn(UserRole role, string userName = "tester")
    {
        _user = new UserAccount
        {
            UserName = userName,
            DisplayName = userName,
            Role = role,
            IsEnabled = true,
        };
        _effectiveRole = role;

        Raise(SessionChangeKind.Login, "测试登录");
    }

    public void Start(UserAccount user)
    {
        _user = user;
        _effectiveRole = user.Role;
        Raise(SessionChangeKind.Login, null);
    }

    public void Logout(string reason = "主动退出")
    {
        var previous = _user;
        _user = null;
        _effectiveRole = UserRole.Operator;

        Changed?.Invoke(this, new SessionChangedEventArgs
        {
            Kind = SessionChangeKind.Logout,
            User = null,
            PreviousUser = previous,
            EffectiveRole = UserRole.Operator,
            Reason = reason,
        });

        // 真实链路里这一步由 SessionBridge 完成（把事件搬成消息）。
        // 替身直连消息，省掉桥本身 —— 测试要验的是"界面收到变化后的反应"。
        WeakReferenceMessenger.Default.Send(new SessionChangedMessage(
            null, UserRole.Operator, SessionChangeKind.Logout, reason));
    }

    public void Touch()
    {
        // 替身不做计时
    }

    public bool HasPermission(Permission permission)
        => _user is null
            ? permission == Permission.None
            : RolePermissions.Has(_effectiveRole, permission);

    /// <summary>模拟"权限被降级"，用于验界面是否跟着退出工程师模式</summary>
    public void Downgrade()
    {
        if (_user is null) return;
        _effectiveRole = UserRole.Operator;
        Raise(SessionChangeKind.Downgraded, "测试降级", isAutomatic: true);
    }

    private void Raise(SessionChangeKind kind, string? reason, bool isAutomatic = false)
    {
        var args = new SessionChangedEventArgs
        {
            Kind = kind,
            User = _user,
            EffectiveRole = _effectiveRole,
            IsAutomatic = isAutomatic,
            Reason = reason,
        };

        Changed?.Invoke(this, args);
        WeakReferenceMessenger.Default.Send(new SessionChangedMessage(
            _user, _effectiveRole, kind, reason));
    }
}

/// <summary>
/// 登录窗替身：不创建窗口，只记录"被要求登录过几次"，并让测试决定结果。
/// </summary>
internal sealed class FakeLoginPrompt : ILoginPrompt
{
    /// <summary>下一次弹窗的结果</summary>
    public bool NextResult { get; set; }

    /// <summary>弹窗被调用时执行（通常用来模拟"登录后会话生效"）</summary>
    public Action? OnShown { get; set; }

    public int ShowCount { get; private set; }

    public List<string?> Reasons { get; } = new();

    public bool ShowLogin(string? reason = null)
    {
        ShowCount++;
        Reasons.Add(reason);

        if (!NextResult) return false;

        OnShown?.Invoke();
        return true;
    }
}
