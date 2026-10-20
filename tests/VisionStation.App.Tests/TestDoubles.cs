using System.ComponentModel;
using VisionStation.App.Abstractions;
using VisionStation.App.Services.Coordinates;
using VisionStation.App.Services.Navigation;
using VisionStation.App.ViewModels;
using VisionStation.Core.Models;
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

/// <summary>相机替身：只数 Capture 次数，不产生图像。</summary>
internal sealed class FakeCamera : ICamera
{
    public bool IsAvailable { get; set; } = true;
    public int CaptureCount { get; private set; }
    public int FrameCount => CaptureCount;

    public void open() { }
    public void close() { }

    public CameraFrame? Capture()
    {
        CaptureCount++;
        return null;   // 采不到图，调用方应跳过本帧而不是抛异常
    }

    public void Dispose() { }
}

/// <summary>仓储替身：关闭流程不关心数据库内容，只保证接口齐。</summary>
internal sealed class FakeInspectionRepository : IInspectionRepository
{
    public Task EnsureCreatedAsync() => Task.CompletedTask;

    public Task SaveAsync(InspectionReport report) => Task.CompletedTask;

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
    public Task<string> SaveAsync(RecipeConfig recipe, CancellationToken ct = default)
        => Task.FromResult(@"C:/recipes/test.json");
}

/// <summary>页面 VM 的组装助手：测试里只造"能构造出来"的对象，不碰 WPF。</summary>
internal static class TestPages
{
    public static InspectionPageViewModel Inspection() => new(
        new ImagePanelViewModel(),
        new ResultPanelViewModel(),
        new LogPanelViewModel(),
        new ControlPanelViewModel(new FakeTriggerSource(), new FakeCamera(), new NoopDialog()),
        new RoiEditorViewModel(new CoordinateMapper(), new RecipeConfig(), new NoopRecipeStore(), new NoopDialog()));

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
