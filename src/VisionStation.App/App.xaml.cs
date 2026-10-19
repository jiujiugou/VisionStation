using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using VisionStation.App.Services;
using VisionStation.App.Services.Coordinates;
using VisionStation.Service;
using VisionStation.Service.Housekeeping;
using VisionStation.App.ViewModels;
using VisionStation.App.Views;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;
using VisionStation.Devices;
using VisionStation.Storage;

namespace VisionStation.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>崩溃留痕用：日志目录解析出来后写入，崩溃处理器据此落盘</summary>
    private static AppPaths? _paths;

    /// <summary>正常关闭信号：看门狗据此区分"正常退出"与"崩溃"，收到即结束不自拉</summary>
    private static EventWaitHandle? _shutdownSignal;

    /// <summary>启动是否走完 —— 只有走完的正常关闭才算 clean，启动失败要留给看门狗重试</summary>
    private static bool _startupCompleted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // UI 线程未处理异常：留痕后交回运行时（Handled=false）→ 进程以非 0 码终止 → 看门狗拉起。
        // 这里绝不能弹模态框：模态框会卡住 Dispatcher，进程不退出，
        // 看门狗的 WaitForExit/HasExited 永远等不到，守护彻底失效。
        DispatcherUnhandledException += (_, args) =>
        {
            Fatal("ui", args.Exception);
            args.Handled = false;
        };
        // 后台 Task 未观察异常：非致命，仅留痕并标记已观察，防止 GC 终结器把进程带崩
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("task", args.Exception);
            args.SetObserved();
        };
        // 非 UI 线程未处理异常：进程即将终止，最后机会释放相机。
        // 同样只记录、不弹窗 —— 该处理器运行在崩溃线程上，任何阻塞都会拖住 CLR 终止进程。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Fatal("domain", ex);
        };
        try
        {
            // ── 启动看门狗 ──
            StartWatchdog();

            var config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            // ── 统一路径解析：相对路径 → 绝对路径（见 AppPaths）──
            var appPaths = new AppPaths(config);
            _paths = appPaths;

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .WriteTo.File(Path.Combine(appPaths.LogFolder, "visionstation-.log"),
                    rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
                .CreateLogger();

            var services = new ServiceCollection();
            services.AddLogging(b => b.AddSerilog(dispose: true));

            // ── 加载配方文件 ──
            var recipe = LoadRecipe(appPaths.RecipePath);

            // ── 模块注册 ──
            services
                .AddSingleton(appPaths)
                .AddCore(recipe, appPaths)
                .AddDevices(appPaths, config)
                .AddStorage(appPaths, config)
                .AddAppServices(appPaths, config)
                .AddViews();

            Services = services.BuildServiceProvider();

            var log = Services.GetRequiredService<ILogger<App>>();
            var health = Services.GetRequiredService<IHealthChecker>();
            foreach (var r in health.RunAll())
            {
                if (r.OK)
                    log.LogInformation("自检 {Item}: OK ({Detail})", r.Item, r.Detail);
                else
                {
                    log.LogError("自检 {Item}: FAIL ({Error})", r.Item, r.Error);
                    MessageBox.Show($"{r.Item}: {r.Error}", "自检失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            // ── 启动后台管家 ──
            Services.GetRequiredService<IHousekeepingService>();

            Services.GetRequiredService<IInspectionRepository>().EnsureCreatedAsync().GetAwaiter().GetResult();
            Services.GetRequiredService<MainWindow>().Show();
            _startupCompleted = true;
        }
        catch (Exception ex)
        {
            // 启动失败按异常退出处理（非 0 码）：看门狗会尝试拉起；
            // 若为确定性失败（配方损坏等），由看门狗的崩溃循环保护兜底，不会无限抖动。
            // 不弹模态框 —— 无人值守时对话框会卡住进程，让看门狗误以为系统还在跑。
            LogCrash("startup", ex);
            Shutdown(1);
        }
    }

    /// <summary>
    /// 加载配方 JSON；文件缺失/损坏/为空时回退到内嵌的默认配方。
    /// 内嵌资源与 Config/recipes/default.json 是同一份文件 —— 兜底内容不可能与磁盘配方不一致。
    /// （旧实现是在代码里手抄一份兜底配方，标定参数和 ROI 早已和 default.json 对不上，
    ///   一旦命中兜底，尺寸测量全错且界面无任何异常 —— 已改为单一真相源）
    /// </summary>
    private static RecipeConfig LoadRecipe(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                // 配方 JSON 用 camelCase 键：必须大小写不敏感，否则 rois/tools 绑不上，静默走兜底配方
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var recipe = JsonSerializer.Deserialize<RecipeConfig>(json, options);
                if (recipe != null && recipe.Tools.Count > 0) return recipe;
            }
        }
        catch (Exception ex)
        {
            LogCrash("recipe", ex);
        }

        // 兜底路径必须留痕：操作员要知道现在跑的是默认配方，不是磁盘上那份
        Log.Warning("配方 {Path} 加载失败（缺失/损坏/为空），回退到内嵌默认配方", path);
        return LoadEmbeddedRecipe();
    }

    /// <summary>从程序集内嵌资源读默认配方 —— 与 Config/recipes/default.json 同一份文件</summary>
    private static RecipeConfig LoadEmbeddedRecipe()
    {
        const string resourceName = "VisionStation.App.Config.recipes.default.json";
        using var stream = typeof(App).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"内嵌配方资源缺失: {resourceName}（打包配置被改动过？）");
        using var reader = new StreamReader(stream);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var recipe = JsonSerializer.Deserialize<RecipeConfig>(reader.ReadToEnd(), options);
        if (recipe == null || recipe.Tools.Count == 0)
            throw new InvalidOperationException("内嵌默认配方内容为空或无效");
        return recipe;
    }

    /// <summary>
    /// 启动看门狗进程 —— 守护本进程，崩了自动拉起。
    /// 同时创建"正常关闭"命名事件：看门狗以此区分正常退出（不重启）与崩溃（重启）。
    /// </summary>
    private static void StartWatchdog()
    {
        try
        {
            var watchdogPath = Path.Combine(AppContext.BaseDirectory, "VisionStation.Watchdog.exe");
            if (!File.Exists(watchdogPath)) return;

            // 事件名固定，每次启动 Reset 清除上一代遗留的信号
            _shutdownSignal = new EventWaitHandle(false, EventResetMode.ManualReset, "VisionStation.Shutdown");
            _shutdownSignal.Reset();

            var myExe = Process.GetCurrentProcess().MainModule?.FileName ?? "VisionStation.App.exe";
            Process.Start(watchdogPath, $"{Environment.ProcessId} \"{myExe}\"");
        }
        catch { /* 看门狗启动失败不影响主程序 */ }
    }

    /// <summary>
    /// 致命异常：进程即将终止。尽力释放设备后留痕。
    /// 绝不做任何可能阻塞的操作（尤其不弹模态框）—— 崩溃处理器一旦阻塞，
    /// 进程不会退出，看门狗的 WaitForExit 就永远等不到退出信号。
    /// </summary>
    private static void Fatal(string kind, Exception ex)
    {
        try { (Services as IDisposable)?.Dispose(); } catch { }
        LogCrash(kind, ex);
    }

    /// <summary>
    /// 崩溃留痕：写 Serilog + 追加崩溃文件（带时间戳，保留历史）。
    /// 自身异常一律吞掉 —— 留痕路径再抛异常会吞掉原始崩溃信息。
    /// </summary>
    private static void LogCrash(string kind, Exception ex)
    {
        try { Log.Fatal(ex, "未处理异常 ({Kind})", kind); } catch { }

        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {kind}\n{ex}\n\n";
        foreach (var folder in CrashFolders())
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "crash.log"), line);
                return;
            }
            catch { /* 换下一个候选目录 */ }
        }
    }

    /// <summary>崩溃文件落盘目录候选：优先 Logs，回退 exe 目录、工作目录</summary>
    private static IEnumerable<string> CrashFolders()
    {
        if (!string.IsNullOrWhiteSpace(_paths?.LogFolder)) yield return _paths!.LogFolder;
        yield return AppContext.BaseDirectory;
        yield return Environment.CurrentDirectory;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 释放 DI 容器持有的 IDisposable（HikCamera 等）——
        // 不释放会占用相机句柄，下次启动 MV_CC_OpenDevice 报"设备被占用"
        (Services as IDisposable)?.Dispose();
        Log.CloseAndFlush();

        // 只有启动走完的正常关闭才发信号；启动失败不发 ——
        // 否则看门狗会把"启动期崩溃"当成正常退出，不再重试
        if (_startupCompleted)
        {
            try { _shutdownSignal?.Set(); } catch { }
        }

        base.OnExit(e);
    }
}
