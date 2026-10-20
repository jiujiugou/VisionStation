using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VisionStation.App.Composition;
using VisionStation.App.Composition.Lifecycle;
using VisionStation.App.Services;
using VisionStation.App.ViewModels;
using VisionStation.Service;

namespace VisionStation.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>崩溃留痕用：日志目录解析出来后写入，崩溃处理器据此落盘</summary>
    private static AppPaths? _paths;

    /// <summary>正常关闭信号：看门狗据此区分"正常退出"与"崩溃"，收到即结束不自拉</summary>
    private static EventWaitHandle? _shutdownSignal;

    /// <summary>组合根。OnExit 的兜底收尾要用它（正常关窗路径已经走过，幂等）。</summary>
    private static AppHost? _host;

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

        // 启动体必须异步（W-20：建库不能卡 UI 线程），而 OnStartup 的签名是 void，
        // 重写不能改成 async Task。这里显式 fire-and-forget：StartAsync 方法体第一行起就
        // 被 try/catch 罩住，失败走 Shutdown(1)；即使有漏网异常也只会进上面的
        // UnobservedTaskException（记痕、不崩进程），不会像 async void 那样直接掀翻进程。
        _ = StartAsync();
    }

    /// <summary>
    /// 启动体。声明为 static 有两个实际理由：它不依赖任何实例成员，
    /// 而且静态方法里写静态字段是唯一能如实表达"这些是进程级状态"的写法。
    /// </summary>
    private static async Task StartAsync()
    {
        try
        {
            StartWatchdog();

            // 看门狗信号通过委托注入：是否算"正常退出"取决于 _startupCompleted（启动失败仍要让看门狗重试）。
            // 放在 App 而不是 AppHost，避免组合根反向依赖 WPF 入口类。
            var host = new AppHost(new DelegateWatchdogSignal(NotifyWatchdogCleanExit));
            _host = host;
            host.Configure();
            _paths = host.Paths;
            Services = host.Services;

            // W-20：建库不再在 UI 线程上同步等待。原来 .GetAwaiter().GetResult()
            // 会把 Dispatcher 卡死整个冷启动，异常路径还有死锁风险；改 await 后
            // 等待期间消息循环照常泵消息。
            await host.InitializeAsync(CancellationToken.None);

            host.Show();
            ReportHealthFailures(host.HealthResults);
            _startupCompleted = true;
        }
        catch (Exception ex)
        {
            // 启动失败按异常退出处理（非 0 码）：看门狗会尝试拉起；
            // 若为确定性失败（配方损坏等），由看门狗的崩溃循环保护兜底，不会无限抖动。
            // 不弹模态框 —— 无人值守时对话框会卡住进程，让看门狗误以为系统还在跑。
            LogCrash("startup", ex);
            Current?.Shutdown(1);
        }
    }

    /// <summary>
    /// 自检失败上报 —— 启动路径不再弹模态框。
    ///
    /// 原来每项失败弹一个 MessageBox：无人值守时对话框会卡住 Dispatcher，
    /// 进程不退出，看门狗的 WaitForExit 永远等不到退出信号，守护形同虚设。
    /// 改为：文件日志（AppHost.InitializeAsync 已逐项写 Error）+ 状态栏文案 + 日志面板。
    /// </summary>
    private static void ReportHealthFailures(IReadOnlyList<HealthResult> results)
    {
        var failures = results.Where(r => !r.OK).ToList();
        if (failures.Count == 0) return;

        try
        {
            var statusBar = Services.GetService<StatusBarViewModel>();
            if (statusBar != null)
                statusBar.StatusText = HealthSummary.Describe(failures);

            foreach (var f in failures)
                WeakReferenceMessenger.Default.Send(new LogMessage(
                    $"自检失败 {f.Item}: {f.Error}", LogMsgLevel.Error));
        }
        catch (Exception ex)
        {
            // 上报失败不能反过来把启动带走；原始失败项已经进了文件日志
            Log.Warning(ex, "自检失败上报到界面时出错");
        }
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
        // 兜底路径（启动失败 / 系统注销 / 异常退出）。正常关窗已经走过 ShutdownCoordinator，
        // StopAsync 幂等，这里基本立即返回。
        //
        // 这里允许有界阻塞（最多 2 秒）：进程即将结束，没有 UI 需要响应。
        // 但绝不能无条件等待 —— 那会让"启动失败后退不掉"变成新问题。
        try
        {
            _host?.StopAsync(TimeSpan.FromSeconds(2)).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            try { Log.Warning(ex, "退出兜底收尾失败"); } catch { /* 退出路径不再抛 */ }
        }

        // 容器与设备已由 StopAsync 释放（重复释放是安全的）。这两步本身幂等，留着兜底。
        Log.CloseAndFlush();

        // 只有启动走完的正常关闭才发信号；启动失败不发 ——
        // 否则看门狗会把"启动期崩溃"当成正常退出，不再重试
        if (_startupCompleted)
        {
            try { _shutdownSignal?.Set(); } catch { }
        }

        base.OnExit(e);
    }

    /// <summary>
    /// 通知看门狗"本次是正常退出"。由 <c>ShutdownCoordinator</c> 第 6 步调用。
    /// 启动没走完时保持沉默 —— 那是崩溃/失败，看门狗必须重试。
    /// </summary>
    private static void NotifyWatchdogCleanExit()
    {
        if (!_startupCompleted) return;
        try { _shutdownSignal?.Set(); } catch { /* 信号发不出去不能反过来影响退出 */ }
    }
}
