using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VisionStation.App.Composition;
using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Service;
using VisionStation.Service.Housekeeping;
using VisionStation.Storage;
using VisionStation.Storage.Entities;

namespace VisionStation.App.Tests;

public class AppHostTests
{
    [Fact]
    public async Task Configure_DoesNotResolveStartupServicesOrWindow()
    {
        var root = NewRoot();
        var repoResolves = 0;
        var healthResolves = 0;
        var housekeepingResolves = 0;
        var showCount = 0;

        try
        {
            var host = new AppHost(Config(root), services =>
            {
                services.AddSingleton<IInspectionRepository>(_ =>
                {
                    repoResolves++;
                    return new FakeRepository();
                });
                services.AddSingleton<IHealthChecker>(_ =>
                {
                    healthResolves++;
                    return new FakeHealthChecker();
                });
                services.AddSingleton<IHousekeepingService>(_ =>
                {
                    housekeepingResolves++;
                    return new FakeHousekeeping();
                });
            }, _ => new FakeWindow(() => showCount++));

            host.Configure();

            Assert.NotNull(host.Services);
            Assert.Equal(0, repoResolves);
            Assert.Equal(0, healthResolves);
            Assert.Equal(0, housekeepingResolves);
            Assert.Equal(0, showCount);

            await host.StopAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task InitializeAsync_RunsStartupServicesWithoutShowingWindow()
    {
        var root = NewRoot();
        var repo = new FakeRepository();
        var health = new FakeHealthChecker();
        health.Results.Add(new HealthResult("自检", true, "OK"));
        var housekeeping = new FakeHousekeeping();
        var housekeepingResolves = 0;
        var showCount = 0;

        try
        {
            var host = new AppHost(Config(root), services =>
            {
                services.AddSingleton<IInspectionRepository>(repo);
                services.AddSingleton<IHealthChecker>(health);
                services.AddSingleton<IHousekeepingService>(_ =>
                {
                    housekeepingResolves++;
                    return housekeeping;
                });
            }, _ => new FakeWindow(() => showCount++));

            host.Configure();
            await host.InitializeAsync(CancellationToken.None);

            Assert.Equal(1, repo.EnsureCreatedCount);
            Assert.Equal(1, health.RunCount);
            Assert.Equal(1, housekeepingResolves);
            Assert.Same(health.Results, host.HealthResults);
            Assert.Equal(0, showCount);

            await host.StopAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, housekeeping.DisposeCount);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task InitializeAsync_WhenDatabaseFails_ThrowsWithoutShowingWindow()
    {
        var root = NewRoot();
        var repo = new FakeRepository { EnsureFailure = new IOException("db unavailable") };
        var showCount = 0;

        try
        {
            var host = new AppHost(Config(root), services =>
            {
                services.AddSingleton<IInspectionRepository>(repo);
                services.AddSingleton<IHealthChecker>(new FakeHealthChecker());
                services.AddSingleton<IHousekeepingService>(new FakeHousekeeping());
            }, _ => new FakeWindow(() => showCount++));

            host.Configure();

            await Assert.ThrowsAsync<IOException>(
                () => host.InitializeAsync(CancellationToken.None));
            Assert.Equal(0, showCount);

            await host.StopAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// S2.4：设备连接从 DI 工厂搬到 InitializeAsync，且必须早于自检
    /// （HealthChecker 读的是 DeviceManager.AllConnected）。
    /// 这条把"顺序"钉成可执行的断言：自检开始时设备的 Connect 已经跑过一次。
    /// </summary>
    [Fact]
    public async Task InitializeAsync_ConnectsDevicesBeforeHealthCheck()
    {
        var root = NewRoot();
        var device = new RecordingDevice();
        var health = new FakeHealthChecker();
        var connectsSeenByHealth = -1;

        health.OnRun = () => connectsSeenByHealth = device.ConnectCount;

        try
        {
            var host = new AppHost(Config(root), services =>
            {
                var dm = new DeviceManager();
                dm.Register(device);
                services.AddSingleton(dm);          // 后注册覆盖 AddDevices 里的真实 DeviceManager
                services.AddSingleton<IInspectionRepository>(new FakeRepository());
                services.AddSingleton<IHealthChecker>(health);
                services.AddSingleton<IHousekeepingService>(new FakeHousekeeping());
            }, _ => new FakeWindow(() => { }));

            host.Configure();
            await host.InitializeAsync(CancellationToken.None);

            Assert.Equal(1, device.ConnectCount);          // 只连一次，不是"每解析一次连一次"
            Assert.Equal(1, connectsSeenByHealth);         // 自检开始时已经连过
            Assert.Equal(1, health.RunCount);

            await host.StopAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task Show_CreatesAndShowsWindowOnce()
    {
        var root = NewRoot();
        var showCount = 0;

        try
        {
            var host = new AppHost(Config(root), null, _ => new FakeWindow(() => showCount++));

            host.Configure();
            host.Show();
            host.Show();

            Assert.Equal(1, showCount);

            await host.StopAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task StopAsync_CalledTwice_DisposesServicesOnce()
    {
        var root = NewRoot();
        var housekeeping = new FakeHousekeeping();

        try
        {
            var host = new AppHost(Config(root), services =>
            {
                services.AddSingleton<IInspectionRepository>(new FakeRepository());
                services.AddSingleton<IHealthChecker>(new FakeHealthChecker());
                services.AddSingleton<IHousekeepingService>(_ => housekeeping);
            }, _ => new FakeWindow(() => { }));

            host.Configure();
            await host.InitializeAsync(CancellationToken.None);
            await host.StopAsync(TimeSpan.FromSeconds(1));
            await host.StopAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(1, housekeeping.DisposeCount);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task HandleWindowClosing_首次请求_接管并请求退出()
    {
        var root = NewRoot();
        var shutdownCount = 0;

        try
        {
            var host = new AppHost(Config(root), null, _ => new FakeWindow(() => { }),
                requestShutdown: () => shutdownCount++);

            host.Configure();
            host.Show();

            var args = new WindowCloseRequestedEventArgs();
            var handled = await host.HandleWindowClosingAsync(args);

            // C1 + C6：窗口先留住（Cancel），收尾完成后才请求退出，且只请求一次
            Assert.True(handled);
            Assert.True(args.Cancel);
            Assert.Equal(1, shutdownCount);

            await host.StopAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task HandleWindowClosing_重复请求_不重复收尾也不重复退出()
    {
        var root = NewRoot();
        var shutdownCount = 0;

        try
        {
            var host = new AppHost(Config(root), null, _ => new FakeWindow(() => { }),
                requestShutdown: () => shutdownCount++);

            host.Configure();
            host.Show();

            await host.HandleWindowClosingAsync(new WindowCloseRequestedEventArgs());

            var second = new WindowCloseRequestedEventArgs();
            var handledAgain = await host.HandleWindowClosingAsync(second);

            // 第二次仍然要接管（否则窗口会自己关掉、绕过收尾），但不能再收尾、不能再请求退出
            Assert.True(handledAgain);
            Assert.True(second.Cancel);
            Assert.Equal(1, shutdownCount);

            await host.StopAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            Delete(root);
        }
    }

    private static IConfiguration Config(string root) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Camera:Mode"] = "File",
                ["Camera:ImageFolder"] = Path.Combine(root, "TestImages"),
                ["Detection:TemplatePath"] = Path.Combine(root, "template.png"),
                ["Storage:ImageFolder"] = Path.Combine(root, "InspectedImages"),
                ["Storage:DbPath"] = Path.Combine(root, "visionstation.db"),
                ["Logging:LogFolder"] = Path.Combine(root, "Logs"),
                ["Recipe:Path"] = Path.Combine(root, "recipe.json"),
                ["Trigger:TimerIntervalMs"] = "2000"
            })
            .Build();

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisionStation.AppHostTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Delete(string root)
    {
        try { Directory.Delete(root, true); } catch { }
    }

    private sealed class FakeRepository : IInspectionRepository
    {
        public int EnsureCreatedCount { get; private set; }
        public Exception? EnsureFailure { get; init; }

        public Task EnsureCreatedAsync()
        {
            EnsureCreatedCount++;
            return EnsureFailure == null
                ? Task.CompletedTask
                : Task.FromException(EnsureFailure);
        }

        public Task SaveAsync(InspectionReport report) => Task.CompletedTask;

        public Task<List<InspectionRecord>> GetRecentAsync(int count = 100) =>
            Task.FromResult(new List<InspectionRecord>());

        public Task<(int OkCount, int NgCount)> GetStatisticsAsync(DateTime? since = null) =>
            Task.FromResult((0, 0));

        public Task<int> DeleteOlderThanAsync(DateTime cutoff) => Task.FromResult(0);
    }

    private sealed class FakeHealthChecker : IHealthChecker
    {
        public List<HealthResult> Results { get; } = new();
        public int RunCount { get; private set; }

        /// <summary>自检被调用的瞬间要观察什么（用来断言"连接早于自检"这类顺序）</summary>
        public Action? OnRun { get; set; }

        public List<HealthResult> RunAll()
        {
            RunCount++;
            OnRun?.Invoke();
            return Results;
        }
    }

    /// <summary>记录 Connect 次数的设备替身</summary>
    private sealed class RecordingDevice : IDevice
    {
        public string? LastError => null;
        public int ConnectCount { get; private set; }
        public string Name => "recording";
        public string Type => "Fake";
        public bool IsConnected => ConnectCount > 0;
        public string Status => IsConnected ? "在线" : "离线";
        public void Connect() => ConnectCount++;
        public void Disconnect() { }
    }

    private sealed class FakeHousekeeping : IHousekeepingService
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeWindow : IAppWindow
    {
        private readonly Action _onShow;

        public FakeWindow(Action onShow)
        {
            _onShow = onShow;
        }

        public event EventHandler<WindowCloseRequestedEventArgs>? Closing;

        public void Show() => _onShow();

        /// <summary>模拟用户点 X，返回窗口是否被接管（true = 本次关闭被拦下）。</summary>
        public bool RaiseClosing()
        {
            var args = new WindowCloseRequestedEventArgs();
            Closing?.Invoke(this, args);
            return args.Cancel;
        }
    }
}
