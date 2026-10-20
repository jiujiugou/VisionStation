using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using VisionStation.App.Composition.Lifecycle;
using VisionStation.App.Services;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;
using VisionStation.Devices;
using VisionStation.Service;
using VisionStation.Service.Housekeeping;
using VisionStation.Service.Recipes;
using VisionStation.Storage;

namespace VisionStation.App.Composition;

public sealed class AppHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions RecipeJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IConfiguration? _configuration;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly Func<IServiceProvider, IAppWindow> _windowFactory;
    private readonly IWatchdogSignal _watchdog;
    private readonly Action _requestShutdown;

    /// <summary>关闭预算（暂定 8s：在途帧 3s + 容器释放 4s 的余量，待决策后可能调整）。</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);

    private ServiceProvider? _services;
    private AppPaths? _paths;
    private IAppWindow? _window;
    private ShutdownCoordinator? _coordinator;
    private int _stopped;
    private int _closing;

    public AppHost()
        : this(null, null, null)
    {
    }

    /// <summary>生产入口：需要注入看门狗信号（是否发"正常退出"由 App 决定）。</summary>
    public AppHost(IWatchdogSignal? watchdog)
        : this(null, null, null, watchdog)
    {
    }

    internal AppHost(
        IConfiguration? configuration,
        Action<IServiceCollection>? configureServices,
        Func<IServiceProvider, IAppWindow>? windowFactory,
        IWatchdogSignal? watchdog = null,
        Action? requestShutdown = null)
    {
        _configuration = configuration;
        _configureServices = configureServices;
        _windowFactory = windowFactory ?? (sp => sp.GetRequiredService<MainWindow>());
        _watchdog = watchdog ?? new NoopWatchdogSignal();
        // 默认取进程级 Shutdown；测试注入假对象即可断言"收尾完成后确实请求了退出"
        _requestShutdown = requestShutdown ?? (() => System.Windows.Application.Current?.Shutdown());
    }

    public IServiceProvider Services => _services
        ?? throw new InvalidOperationException("AppHost 尚未 Configure");

    public AppPaths Paths => _paths
        ?? throw new InvalidOperationException("AppHost 尚未 Configure");

    public IReadOnlyList<HealthResult> HealthResults { get; private set; } = Array.Empty<HealthResult>();

    public void Configure()
    {
        if (_services != null) return;

        var config = _configuration ?? new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        var appPaths = new AppPaths(config);
        _paths = appPaths;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(Path.Combine(appPaths.LogFolder, "visionstation-.log"),
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
            .CreateLogger();

        var services = new ServiceCollection();
        // dispose: false —— 日志的关闭时机由关闭流程第 5 步统一控制。
        // 交给容器释放会提前拆掉 sink：容器释放发生在第 4 步，之后的关闭摘要（含失败原因）
        // 全部写不出去，等于"关了但没留痕"。
        services.AddLogging(b => b.AddSerilog(dispose: false));

        var recipe = LoadRecipe(appPaths.RecipePath);

        services
            .AddSingleton(appPaths)
            .AddCore(recipe, appPaths)
            .AddDevices(appPaths, config)
            .AddStorage(appPaths, config)
            .AddAppServices(appPaths, config)
            .AddViews();

        _configureServices?.Invoke(services);

        _services = services.BuildServiceProvider();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var services = _services
            ?? throw new InvalidOperationException("AppHost 尚未 Configure");

        var log = services.GetRequiredService<ILogger<App>>();
        var health = services.GetRequiredService<IHealthChecker>();
        var results = health.RunAll();

        foreach (var r in results)
        {
            if (r.OK)
                log.LogInformation("自检 {Item}: OK ({Detail})", r.Item, r.Detail);
            else
                log.LogError("自检 {Item}: FAIL ({Error})", r.Item, r.Error);
        }

        HealthResults = results;

        services.GetRequiredService<IHousekeepingService>();
        await services.GetRequiredService<IInspectionRepository>().EnsureCreatedAsync();

        // 启动服务都解析完了才建关闭编排器：Configure 阶段保持"只注册不解析"的契约。
        _coordinator = CreateShutdownCoordinator(services);
    }

    private ShutdownCoordinator CreateShutdownCoordinator(IServiceProvider services)
    {
        var stages = new AppShutdownStages(
            services,
            services.GetRequiredService<ITriggerSource>(),
            services.GetRequiredService<IAppLifetime>(),
            services.GetRequiredService<IInFlightWorkMonitor>(),
            services.GetRequiredService<ILogShutdown>(),
            _watchdog,
            logger: services.GetService<ILogger<AppShutdownStages>>());

        return new ShutdownCoordinator(stages, services.GetService<ILogger<ShutdownCoordinator>>());
    }

    public void Show()
    {
        if (_window != null) return;

        _window = _windowFactory(Services);
        // S2.3：关闭请求必须经由宿主接管，否则 exit 路径会直接 Dispose 容器（W-21 的成因）
        _window.Closing += OnWindowClosing;
        _window.Show();
    }

    private async void OnWindowClosing(object? sender, WindowCloseRequestedEventArgs e)
    {
        try
        {
            await HandleWindowClosingAsync(e);
        }
        catch (Exception ex)
        {
            // 关闭路径抛异常不允许把进程留成半开：留痕后仍然请求退出
            Log.Error(ex, "关窗处理失败");
            e.Cancel = false;
            _requestShutdown();
        }
    }

    /// <summary>
    /// 关窗处理（可 await 的版本，测试直接调用；事件处理器只是它的 async void 包装）。
    /// 返回 true = 已接管本次关闭（调用方应保持窗口打开直到 Shutdown）。
    ///
    /// 契约：
    ///   C6 首次接管并把窗口留住；重复请求仍接管（否则窗口会自己关掉、绕过收尾），但不再收尾、不再请求退出；
    ///   C1 收尾完成后才请求应用退出 —— 收尾失败也必须退出，不能让窗口卡在那里。
    /// </summary>
    internal async Task<bool> HandleWindowClosingAsync(WindowCloseRequestedEventArgs e)
    {
        e.Cancel = true;

        if (Interlocked.Exchange(ref _closing, 1) == 1)
            return true;

        try
        {
            WeakReferenceMessenger.Default.Send(new LogMessage("正在关闭…", LogMsgLevel.Info));
            await StopAsync(ShutdownTimeout).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 关闭路径不允许把进程留成半开：留痕后照样请求退出
            Log.Error(ex, "关窗收尾失败");
        }

        _requestShutdown();
        return true;
    }

    /// <summary>
    /// 有序关闭。幂等（第二次调用立刻返回）。
    ///
    /// 正常路径委托 <see cref="ShutdownCoordinator"/> 走六步；
    /// 如果从未走完 <c>InitializeAsync</c>（启动失败、配置未完成），只做最小收尾：
    /// 释放容器 + 刷日志，不发看门狗信号（看门狗仍应重试）。
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
            return;

        if (_coordinator == null)
        {
            try
            {
                if (_services is IDisposable disposable) disposable.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放容器失败（降级路径）");
            }

            await Log.CloseAndFlushAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await _coordinator.StopAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Coordinator 自己已经吞掉单步异常；能走到这里的只有编排器本身出问题
            Log.Error(ex, "关闭编排失败");
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync(TimeSpan.FromSeconds(5)));

    private static RecipeConfig LoadRecipe(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var recipe = JsonSerializer.Deserialize<RecipeConfig>(json, RecipeJsonOptions);
                if (recipe != null && recipe.Tools.Count > 0) return recipe;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "配方加载失败 ({Path})", path);
        }

        Log.Warning("配方 {Path} 加载失败（缺失/损坏/为空），回退到内嵌默认配方", path);
        return LoadEmbeddedRecipe();
    }

    private static RecipeConfig LoadEmbeddedRecipe()
    {
        const string resourceName = "VisionStation.App.Config.recipes.default.json";
        using var stream = typeof(AppHost).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"内嵌配方资源缺失: {resourceName}（打包配置被改动过？）");
        using var reader = new StreamReader(stream);
        var recipe = JsonSerializer.Deserialize<RecipeConfig>(reader.ReadToEnd(), RecipeJsonOptions);
        if (recipe == null || recipe.Tools.Count == 0)
            throw new InvalidOperationException("内嵌默认配方内容为空或无效");
        return recipe;
    }
}
