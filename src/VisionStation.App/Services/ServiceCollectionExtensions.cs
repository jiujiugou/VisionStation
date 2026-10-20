using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisionStation.App.Abstractions;
using VisionStation.App.Infrastructure;
using VisionStation.App.Services;
using VisionStation.App.ViewModels;
using VisionStation.App.Views;
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
            var dm = new DeviceManager();
            dm.Register(new CameraDevice(sp.GetRequiredService<ICamera>()));
            dm.Register(new TriggerDevice(sp.GetRequiredService<ITriggerSource>()));
            dm.ConnectAll();
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

    /// <summary>注册 ViewModels + Views</summary>
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
        services.AddSingleton<MainViewModel>();

        services.AddSingleton<SidebarView>();
        services.AddSingleton<ImagePanelView>();
        services.AddSingleton<ResultPanelView>();
        services.AddSingleton<LogPanelView>();
        services.AddSingleton<ControlPanelView>();
        services.AddSingleton<StatusBarView>();
        services.AddSingleton<CameraPageViewModel>();
        services.AddSingleton<CameraPageView>();
        services.AddSingleton<RoiEditorView>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}
