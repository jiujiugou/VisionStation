using System.IO;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionStation.App.Abstractions;
using VisionStation.App.Services;
using VisionStation.App.Composition;
using VisionStation.App.Composition.Lifecycle;
using VisionStation.App.Services.Inspection;
using VisionStation.App.Services.Navigation;
using VisionStation.App.ViewModels;
using VisionStation.Core.Models;
using VisionStation.Core.Engines;
using VisionStation.Devices;
using VisionStation.Storage;
using VisionStation.Service.Recipes;
using VisionStation.Storage.Entities;

namespace VisionStation.App.Tests;

// S2.3 关闭流程的共用替身。
// 刻意不引入 Moq：这些接口都很窄，手写替身能一眼看出"到底被调了几次"，
// 也不必为了一个 bool 去背表达式树的语法。

/// <summary>触发源替身：可手动 Fire，记录 Stop 次数。</summary>
internal sealed class FakeTriggerSource : ITriggerSource
{
    public event Action? Triggered;

    public bool IsRunning { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public int FireCount { get; private set; }

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
        Triggered?.Invoke();
    }

    public void Dispose() { }
}

/// <summary>相机替身：数 Capture 次数。默认采不到图（返回 null）。</summary>
internal sealed class FakeCamera : ICamera
{

    /// <summary>可由测试注入的失败原因（断言自检/DeviceManager 会把它带出来）</summary>
    public string? LastError { get; set; }
    /// <summary>非空时每次采图都调用它产出一帧。注意每次要新建 Mat：工作流会把上一帧释放掉。</summary>
    public Func<CameraFrame?>? FrameFactory { get; set; }

    public bool IsAvailable { get; set; } = true;
    public int CaptureCount { get; private set; }
    public int FrameCount => CaptureCount;

    public void Open() { }
    public void Close() { }

    public CameraFrame? Capture()
    {
        CaptureCount++;
        return FrameFactory?.Invoke();   // 默认 null：调用方应跳过本帧而不是抛异常
    }

    public void Dispose() { }
}

/// <summary>仓储替身：关闭流程不关心数据库内容，只保证接口齐。</summary>
internal sealed class FakeInspectionRepository : IInspectionRepository
{
    public int SaveCount { get; private set; }

    public Task EnsureCreatedAsync() => Task.CompletedTask;

    public Task SaveAsync(InspectionReport report)
    {
        SaveCount++;
        return Task.CompletedTask;
    }

    public Task<List<InspectionRecord>> GetRecentAsync(int count = 100) =>
        Task.FromResult(new List<InspectionRecord>());

    public Task<(int OkCount, int NgCount)> GetStatisticsAsync(DateTime? since = null) =>
        Task.FromResult((0, 0));

    public Task<int> DeleteOlderThanAsync(DateTime cutoff) => Task.FromResult(0);
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

    /// <summary>VM 推送进来的报告（按到达顺序记录），用来断言"外部结果确实走到了工作流"。</summary>
    public List<InspectionReport> ExternalReports { get; } = new();

    public async Task<InspectionRunResult?> RunOnceAsync(CancellationToken ct)
    {
        RunCount++;
        LastToken = ct;

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

    /// <summary>重载被调用次数</summary>
    public int ReloadCount { get; private set; }

    /// <summary>重载返回的失败原因（null = 成功）</summary>
    public string? ReloadProblem { get; set; }

    public string Name => "FakeEngine";

    public InspectionReport Inspect(InspectionRequest request)
    {
        InspectCount++;
        return Factory?.Invoke(request)
            ?? Report
            ?? InspectionReport.Failure(Name, "替身未配置报告", request.TriggerSource, request.ImagePath);
    }

    public string? Reload()
    {
        ReloadCount++;
        return ReloadProblem;
    }

    public void Dispose() { }
}

/// <summary>
/// 支持输出扫描的引擎替身（只有 VM 引擎才有这个能力）。
/// 单独一个类而不是给 <see cref="FakeEngine"/> 加接口：
/// "引擎不支持扫描"本身是一条要测的分支（ONNX 引擎就是这种情况）。
/// </summary>
internal sealed class FakeScanningEngine : IInspectionEngine, IOutputScanner
{
    public OutputScanResult Result { get; set; } = new(Array.Empty<ScannedModule>(), null);

    /// <summary>最近一次扫描收到的模块名</summary>
    public IReadOnlyList<string> LastScanNames { get; private set; } = Array.Empty<string>();

    public string Name => "FakeVm";

    public InspectionReport Inspect(InspectionRequest request)
        => InspectionReport.Failure(Name, "替身不检测", request.TriggerSource, request.ImagePath);

    public string? Reload() => null;

    public OutputScanResult Scan(IReadOnlyList<string> moduleNames)
    {
        LastScanNames = moduleNames;
        return Result;
    }

    public void Dispose() { }
}

/// <summary>测试用的路径与配方：数据目录落临时目录，避免污染 bin。</summary>
internal static class TestPaths
{
    public static AppPaths Create() => new(new AppOptions
    {
        DataRoot = Path.Combine(Path.GetTempPath(), "vs_test_" + Guid.NewGuid().ToString("N")[..8])
    });

    /// <summary>带一个模块声明的配方 —— 扫描命令要有模块名才做事</summary>
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
            CameraOnline: true);
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
        FakeCamera? camera = null,
        ShutdownState? shutdown = null,
        InFlightWorkMonitor? inFlight = null,
        ILogger<InspectionPageViewModel>? logger = null,
        IInspectionEngine? engine = null)
    {
        trigger ??= new FakeTriggerSource();
        camera ??= new FakeCamera();

        return new InspectionPageViewModel(
            new ImagePanelViewModel(),
            new ResultPanelViewModel(),
            new LogPanelViewModel(),
            new ControlPanelViewModel(trigger, camera, new NoopDialog(),
                engine ?? new FakeEngine(), TestPaths.Create(), TestPaths.Recipe()),
            workflow ?? new FakeWorkflow(),
            trigger,
            camera,
            shutdown ?? new ShutdownState(),
            inFlight ?? new InFlightWorkMonitor(),
            logger ?? NullLogger<InspectionPageViewModel>.Instance);
    }

    public static CameraPageViewModel Camera()
    {
        var camera = new FakeCamera();
        var swappable = new SwappableCamera(camera, _ => camera, "File");
        return new CameraPageViewModel(swappable, new DeviceManager());
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
