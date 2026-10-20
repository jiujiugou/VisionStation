using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VisionStation.App.Composition;
using VisionStation.Core.Models;
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

        public List<HealthResult> RunAll()
        {
            RunCount++;
            return Results;
        }
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

        public void Show() => _onShow();
    }
}
