using System.Diagnostics;
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
using VisionStation.App.ViewModels;
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

    /// <summary>关闭收尾完成后通知看门狗"这是正常退出"。启动没走完时由 App 侧自行忽略。</summary>
    private readonly Action? _onCleanExit;

    private readonly Action _requestShutdown;

    /// <summary>关闭预算：在途帧 3s + 容器释放 4s 的余量。</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);

    private ServiceProvider? _services;
    private AppPaths? _paths;
    private IAppWindow? _window;
    private int _stopped;
    private int _closing;

    public AppHost()
        : this(null, null, null)
    {
    }

    /// <summary>生产入口：看门狗信号由 App 决定（启动失败不该发 clean）。</summary>
    public AppHost(Action? onCleanExit)
        : this(null, null, null, onCleanExit)
    {
    }

    internal AppHost(
        IConfiguration? configuration,
        Action<IServiceCollection>? configureServices,
        Func<IServiceProvider, IAppWindow>? windowFactory,
        Action? onCleanExit = null,
        Action? requestShutdown = null)
    {
        _configuration = configuration;
        _configureServices = configureServices;
        // 窗口由这里直接 new：它不属于容器（容器只管 VM 与后台服务）。
        _windowFactory = windowFactory ?? (sp => new MainWindow(sp.GetRequiredService<ShellViewModel>()));
        _onCleanExit = onCleanExit;
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

        // S2.4：设备连接在这里做，不在 DI 工厂里 —— 解析一个对象不该有"打开设备"这种副作用，
        // 否则启动顺序会被"谁先被解析"决定，测试一建容器就会去开真相机。
        // 必须早于自检：HealthChecker 读的是 DeviceManager.AllConnected。
        var devices = services.GetRequiredService<DeviceManager>();
        var connectFailures = devices.ConnectAll();
        var (online, total) = devices.ConnectionStats;
        if (connectFailures.Count == 0)
            log.LogInformation("设备连接完成 {Online}/{Total}", online, total);
        else
            log.LogWarning("设备连接完成 {Online}/{Total}，失败: {Failures}",
                online, total, string.Join("; ", connectFailures));

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

        // MainViewModel 靠构造函数订阅触发信号 —— 没人解析它，就没人采图。
        // S3b 之后它不再被窗口注入，必须在这里显式取一次。
        services.GetRequiredService<MainViewModel>();

        await services.GetRequiredService<IInspectionRepository>().EnsureCreatedAsync();
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
    /// 顺序写死在这里，不拆编排器：WPF 应用只有这一条关闭路径，
    /// 拆成"可注入的六步"只对 mock 有用，对实际运行没有任何好处。
    /// 每步一行带编号的日志就是验证手段（配合一次真实关窗走查）。
    ///
    /// 顺序不能颠倒：先停摄入、再取消、再等在途，最后才拆设备与容器。
    /// 反过来做，在途帧会落在已释放的 DbContext / 相机句柄上。
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
            return;

        var services = _services;
        if (services == null) return;   // Configure 都没走完，没有可收尾的东西

        var budget = Stopwatch.StartNew();

        // 1 停掉定时触发。已进入 Elapsed 的回调仍可能触发，靠第 2 步拦住。
        Guarded("1 stop-trigger", () => services.GetService<ITriggerSource>()?.Stop(), budget);

        // 2 应用级取消：MainViewModel 据此拒绝排队中的触发。
        Guarded("2 cancel", () => services.GetService<ShutdownState>()?.BeginStop(), budget);

        // 3 等在途帧。必须有上界；等不到就如实记 timeout，不能假装收尾干净。
        var inFlight = services.GetService<InFlightWorkMonitor>();
        if (inFlight != null)
        {
            var remaining = timeout - budget.Elapsed;
            var idle = false;

            try
            {
                idle = remaining > TimeSpan.Zero
                    && await inFlight.WhenIdleAsync(remaining).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[关闭] 3 在途收尾异常");
            }

            Log.Information("[关闭] 3 在途收尾: {Result}（{Elapsed}ms）",
                idle ? "ok" : "timeout", budget.ElapsedMilliseconds);
        }

        // 4 释放设备与容器。必须在后台线程：HousekeepingService.Dispose 内部会同步等最多 5 秒，
        // 在 UI 线程上释放等于关窗时把界面冻住。
        try
        {
            await Task.Run(() => (services as IDisposable)?.Dispose()).ConfigureAwait(false);
            Log.Information("[关闭] 4 释放容器（{Elapsed}ms）", budget.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[关闭] 4 释放容器失败");
        }

        // 5 刷日志。摘要必须写在这一行之前：CloseAndFlush 之后 sink 就关了。
        Log.Information("[关闭] 5 刷日志（总 {Elapsed}ms）", budget.ElapsedMilliseconds);
        await Log.CloseAndFlushAsync().ConfigureAwait(false);

        // 6 通知看门狗。这之后不能再写日志（sink 已关）。
        try { _onCleanExit?.Invoke(); } catch { /* 信号发不出去不能反过来影响退出 */ }
    }

    /// <summary>单步执行：失败只留痕，不阻断后续（关闭是尽力而为）。</summary>
    private static void Guarded(string step, Action action, Stopwatch budget)
    {
        try
        {
            action();
            Log.Information("[关闭] {Step}（{Elapsed}ms）", step, budget.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[关闭] {Step} 失败", step);
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
