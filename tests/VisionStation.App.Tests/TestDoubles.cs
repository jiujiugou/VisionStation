using VisionStation.App.Composition.Lifecycle;
using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Storage;
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

/// <summary>日志收尾替身：证明第 5 步真的被调到。</summary>
internal sealed class FakeLogShutdown : ILogShutdown
{
    public int Count { get; private set; }

    public void CloseAndFlush() => Count++;
}

/// <summary>看门狗信号替身：证明第 6 步真的被调到，且只调一次。</summary>
internal sealed class FakeWatchdogSignal : IWatchdogSignal
{
    public int Count { get; private set; }

    public void NotifyCleanExit() => Count++;
}

/// <summary>可观察的 IDisposable：记录是否被释放、在哪条线程上释放。</summary>
internal sealed class FakeDisposable : IDisposable
{
    private readonly Action _onDispose;

    public FakeDisposable(Action onDispose) => _onDispose = onDispose;

    public void Dispose() => _onDispose();
}
