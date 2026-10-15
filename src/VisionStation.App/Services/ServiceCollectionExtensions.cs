using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisionStation.App.Services;
using VisionStation.App.ViewModels;
using VisionStation.App.Views;
using VisionStation.App.Services.Coordinates;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;
using VisionStation.Devices;
using VisionStation.Service;
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
            return new SwappableCamera(factory.Create(), mode => factory.CreateByMode(mode));
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
        services.AddSingleton(new InspectionDbContext(
            appPaths.DbPath));
        services.AddSingleton<InspectionRepository>();
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

        services.AddSingleton<IHousekeepingService>(sp => new HousekeepingService(
            sp.GetRequiredService<InspectionRepository>(),
            appPaths.StorageFolder,
            config,
            sp.GetService<ILogger<HousekeepingService>>()));

        return services;
    }

    /// <summary>注册 ViewModels + Views</summary>
    public static IServiceCollection AddViews(this IServiceCollection services)
    {
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
