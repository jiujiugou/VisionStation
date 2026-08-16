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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            File.WriteAllText("crash.log", args.Exception.ToString());
            MessageBox.Show(args.Exception.Message, "UI线程异常", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = false;
        };
        //后台task
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            File.WriteAllText("crash_task.log", args.Exception.ToString());
            args.SetObserved();
        };
        //非UI线程
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                File.WriteAllText("crash_domain.log", ex.ToString());
                MessageBox.Show(ex.Message, "非UI线程异常", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

            Services.GetRequiredService<InspectionRepository>().EnsureCreatedAsync().GetAwaiter().GetResult();
            Services.GetRequiredService<MainWindow>().Show();
        }
        catch (Exception ex)
        {
            File.WriteAllText("crash_startup.log", ex.ToString());
            MessageBox.Show(ex.ToString(), "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>加载配方 JSON，文件损坏或缺失返回内置默认值</summary>
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
            File.WriteAllText("crash_recipe.log", ex.ToString());
        }

        // 兜底：内置默认配方（ROI + Tools 齐全）
        return new RecipeConfig
        {
            Name = "默认配方",
            Calibration = new() { PixelPerMillimeter = 15.0, DesignedDiameterMm = 2.67, DimensionToleranceMm = 0.50 },
            // 兜底配方的 ROI 必须与 default.json 一致，否则工具名匹配不上，Pipeline 会全部落在 240×240 大框上
            Rois = new()
            {
                new() { Name = "Presence",  X = 120, Y = 120, Width = 80, Height = 80 },
                new() { Name = "Dimension", X = 120, Y = 120, Width = 80, Height = 80 },
                new() { Name = "Defect",    X = 30,  Y = 30,  Width = 240, Height = 240 }
            },
            Tools = new()
            {
                new() { Type = "Presence", Enabled = true, Roi = "Presence", Params = new() { ["threshold"] = 0.70 } },
                new() { Type = "Dimension", Enabled = true, Roi = "Dimension", Params = new() { ["tolerance"] = 0.50 } },
                new() { Type = "Defect", Enabled = true, Roi = "Defect", Params = new() { ["areaMin"] = 50, ["thresholdValue"] = 128 } }
            }
        };
    }

    /// <summary>
    /// 启动看门狗进程 —— 守护本进程，崩了自动拉起
    /// </summary>
    private static void StartWatchdog()
    {
        try
        {
            var watchdogPath = Path.Combine(AppContext.BaseDirectory, "VisionStation.Watchdog.exe");
            if (!File.Exists(watchdogPath)) return;

            var myExe = Process.GetCurrentProcess().MainModule?.FileName ?? "VisionStation.App.exe";
            Process.Start(watchdogPath, $"{Environment.ProcessId} \"{myExe}\"");
        }
        catch { /* 看门狗启动失败不影响主程序 */ }
    }

    protected override void OnExit(ExitEventArgs e) { Log.CloseAndFlush(); base.OnExit(e); }
}
