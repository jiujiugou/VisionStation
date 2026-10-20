using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisionStation.App.Abstractions;
using VisionStation.App.Composition.Lifecycle;
using VisionStation.App.Infrastructure;
using VisionStation.App.Services;
using VisionStation.App.Services.Inspection;
using VisionStation.App.Services.Navigation;
using VisionStation.App.ViewModels;
using VisionStation.App.Services.Coordinates;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;
using VisionStation.Devices;
using VisionStation.Service;
using VisionStation.Service.Housekeeping;
using VisionStation.Service.Recipes;
using VisionStation.Storage;

namespace VisionStation.App;

/// <summary>
/// DI 扩展方法 —— 按模块分组注册，App.xaml.cs 一行调用一个
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>注册 Core 层：标定、Pipeline、Tools</summary>
    public static IServiceCollection AddCore(this IServiceCollection services,
        RecipeConfig recipe, AppPaths appPaths)
    {
        var calib = new CalibrationConfig
        {
            PixelPerMillimeter = recipe.Calibration.PixelPerMillimeter,
            DesignedDiameterMm = recipe.Calibration.DesignedDiameterMm,
            DimensionToleranceMm = recipe.Calibration.DimensionToleranceMm
        };
        services.AddSingleton(calib);
        services.AddSingleton(recipe);

        var calibration = new Calibration();
        calibration.SetConfig(calib);
        services.AddSingleton(calibration);

        var toolFactory = new ToolFactory(calibration, recipe,
            appPaths.TemplatePath);
        var tools = toolFactory.CreateAll();

        services.AddSingleton<Pipeline>(sp => new Pipeline(tools,
            sp.GetService<ILogger<Pipeline>>()));

        return services;
    }

    /// <summary>注册 Devices 层：相机、触发源、设备管理器</summary>
    public static IServiceCollection AddDevices(this IServiceCollection services,
        AppPaths appPaths, IConfiguration config)
    {
        services.AddSingleton<CameraFactory>(sp =>
            new CameraFactory(appPaths, config, sp.GetService<ILoggerFactory>()));

        services.AddSingleton<SwappableCamera>(sp =>
        {
            var factory = sp.GetRequiredService<CameraFactory>();
            return new SwappableCamera(factory.Create(),
            mode => factory.CreateByMode(mode),
            config["Camera:Mode"] ?? "File");   // 初始模式必须显式传入：写死默认值会让 UI 的去重守卫误判
        });
        services.AddSingleton<ICamera>(sp => sp.GetRequiredService<SwappableCamera>());

        services.AddSingleton<ITriggerSource>(sp =>
            new TimerTrigger(int.Parse(config["Trigger:TimerIntervalMs"] ?? "2000")));

        services.AddSingleton<DeviceManager>(sp =>
        {
            // 只注册、不连接（W-01/S2.4）：解析一个对象不该有"打开设备"这种副作用。
            // 连接由 AppHost.InitializeAsync 显式做一次，顺序在自检之前。
            var dm = new DeviceManager();
            dm.Register(new CameraDevice(sp.GetRequiredService<ICamera>()));
            dm.Register(new TriggerDevice(sp.GetRequiredService<ITriggerSource>()));
            return dm;
        });

        return services;
    }

    /// <summary>注册 Storage 层：数据库、图片存储</summary>
    public static IServiceCollection AddStorage(this IServiceCollection services,
        AppPaths appPaths, IConfiguration config)
    {
        // 修复（F-111）：不再注册全进程单例 InspectionDbContext。
        // 管家清理线程与检测保存线程会并发用它，EF Core 禁止同一 context 并发操作。
        // Repository 现在每个操作自建/自释 context，所以注册为单例也安全。
        services.AddSingleton<IInspectionRepository>(sp =>
            new InspectionRepository(appPaths.DbPath, sp.GetService<ILogger<InspectionRepository>>()));
        services.AddSingleton(sp =>
        {
            var s = new ImageStoreService(appPaths.StorageFolder);
            s.SetMode((config["Storage:Mode"] ?? "All") switch
            { "NGOnly" => ImageStoreMode.NGOnly, "Sampling" => ImageStoreMode.Sampling, _ => ImageStoreMode.All });
            return s;
        });

        return services;
    }

    /// <summary>注册 Service 层：健康检查、后台管家</summary>
    public static IServiceCollection AddAppServices(this IServiceCollection services,
        AppPaths appPaths, IConfiguration config)
    {
        // S2.3：关闭用的两个单例。都是普通类，不挂接口 —— 这里只有一个实现，接口除了让测试好写没别的用。
        services.AddSingleton<ShutdownState>();
        services.AddSingleton<InFlightWorkMonitor>();

        services.AddSingleton<IHealthChecker>(sp =>
        {
            var recipe = sp.GetRequiredService<RecipeConfig>();
            return new HealthChecker(
                sp.GetRequiredService<DeviceManager>(),
                appPaths.DbPath,
                appPaths.StorageFolder,
                appPaths.TemplatePath,
                recipe);
        });

        // 管家服务 2026-10-19 迁入 Service 层（VisionStation.Service.Housekeeping）——
        // 运维逻辑不住 UI 层。阈值在这里从 appsettings.json 解析，Service 层只收明确参数。
        services.AddSingleton<IHousekeepingService>(sp => new HousekeepingService(
            sp.GetRequiredService<IInspectionRepository>(),
            appPaths.StorageFolder,
            ReadHousekeepingOptions(config),
            sp.GetService<ILogger<HousekeepingService>>()));

        // 检测链路（采图→检测→标注→存图→存库）从 VM 移到 App 服务层（S4a）。
        // 它不引用任何界面类型，只有位图转换会产出 WPF 的 BitmapSource —— 那属于呈现适配。
        services.AddSingleton<IInspectionWorkflow, InspectionWorkflow>();

        // 配方保存从 VM 移到 Service：VM 只表达“保存”，不碰路径、JSON 和临时文件。
        services.AddSingleton<IRecipeStore>(sp => new JsonRecipeStore(
            appPaths.RecipePath,
            sp.GetService<ILogger<JsonRecipeStore>>()));

        return services;
    }

    /// <summary>从 appsettings.json 读管家阈值，缺项回退默认值</summary>
    private static HousekeepingOptions ReadHousekeepingOptions(IConfiguration config)
    {
        var d = HousekeepingOptions.Default;
        return new HousekeepingOptions(
            int.Parse(config["Housekeeping:MaxMemoryMB"] ?? $"{d.MaxMemoryMb}"),
            int.Parse(config["Housekeeping:KeepRecordsDays"] ?? $"{d.KeepRecordsDays}"),
            int.Parse(config["Housekeeping:ImageRetentionDays"] ?? $"{d.ImageRetentionDays}"),
            long.Parse(config["Housekeeping:MaxImageFolderMB"] ?? $"{d.MaxImageFolderBytes / 1024 / 1024}") * 1024 * 1024,
            int.Parse(config["Housekeeping:MemoryCheckIntervalMs"] ?? $"{d.MemoryCheckIntervalMs}"),
            int.Parse(config["Housekeeping:DatabaseCleanIntervalMs"] ?? $"{d.DatabaseCleanIntervalMs}"));
    }

    /// <summary>
    /// 注册 ViewModels（S3b 起不再注册任何 View —— 视图由 App.xaml 的 DataTemplate 创建，
    /// 容器里出现 Window / UserControl 就是错误：那等于让 DI 去管可视树的生命周期）。
    ///
    /// 这里全部是 Singleton，理由是"这些对象都活到进程结束"：
    /// 面板 VM 订阅检测结果消息并与界面绑定（Transient 会多出一份"界面上没显示"的实例），
    /// 页面是常驻工作台（不是"打开某条记录"式的多实例页面）。
    /// </summary>
    public static IServiceCollection AddViews(this IServiceCollection services)
    {
        services.AddSingleton<IDialogService, WpfDialogService>();
        services.AddSingleton<SidebarViewModel>();
        services.AddSingleton<ImagePanelViewModel>();
        services.AddSingleton<ResultPanelViewModel>();
        services.AddSingleton<LogPanelViewModel>();
        services.AddSingleton<ControlPanelViewModel>();
        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<CoordinateMapper>();
        services.AddSingleton<RoiEditorViewModel>();

        // UI 线程的同步上下文 —— 整条检测链路只有这一个"回到 UI 线程"的接缝。
        // 由组合根在 UI 线程上捕获：页面 VM 的工作是把触发排进这个上下文，
        // 之后 await 的续体就落在 UI 线程，消息广播与面板更新才是安全的。
        // 拿不到就直接失败：留到运行时会表现成"第三帧开始随机抛跨线程异常"，极难查。
        services.AddSingleton<SynchronizationContext>(_ => SynchronizationContext.Current
            ?? throw new InvalidOperationException(
                "AddViews 必须在 UI 线程上调用：拿不到同步上下文，检测结果会回到线程池上更新界面。"));

        services.AddSingleton<InspectionPageViewModel>();

        services.AddSingleton<CameraPageViewModel>();

        // 导航：路由表在 NavigationService 里写死两页，不查容器、不建工厂。
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ShellViewModel>();

        return services;
    }
}
