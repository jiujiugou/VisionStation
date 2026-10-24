using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisionStation.App.Abstractions;
using VisionStation.App.Composition;
using VisionStation.App.Composition.Lifecycle;
using VisionStation.App.Infrastructure;
using VisionStation.App.Services;
using VisionStation.App.Services.Inspection;
using VisionStation.App.Services.Navigation;
using VisionStation.App.ViewModels;
using VisionStation.Core.Engines;
using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Devices.Plc;
using VisionStation.Service;
using VisionStation.Service.Audit;
using VisionStation.Service.Housekeeping;
using VisionStation.Service.Recipes;
using VisionStation.Service.Security;
using VisionStation.Storage;

namespace VisionStation.App;

/// <summary>
/// DI 扩展方法 —— 按模块分组注册，App.xaml.cs 一行调用一个
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册检测引擎（配方 + 引擎）。全系统只有这一处决定"用哪套检测"。
    ///
    /// 改造前这里造的是 Calibration + ToolFactory + Pipeline；
    /// 现在只造一个 <see cref="IInspectionEngine"/> —— 判定逻辑要么在 VM 流程里，
    /// 要么在 ONNX 引擎里，组合根不再持有任何算法参数。
    /// </summary>
    public static IServiceCollection AddDetection(this IServiceCollection services,
        RecipeConfig recipe, AppPaths appPaths)
    {
        services.AddSingleton(recipe);

        services.AddSingleton<IInspectionEngine>(sp => InspectionEngineFactory.Create(
            recipe,
            appPaths.AssetRoot,
            sp.GetService<ILoggerFactory>()));

        return services;
    }

    /// <summary>
    /// 注册 VM 结果链路（TCP 中转）。
    ///
    /// 只在配方引擎是 Vm、且 appsettings 里 VmLink:Enabled=true 时才真正 Start() ——
    /// 见 <see cref="Composition.AppHost.InitializeAsync"/>。注册本身无副作用（不占端口）。
    /// </summary>
    public static IServiceCollection AddVmLink(this IServiceCollection services, AppOptions options)
    {
        services.AddSingleton(sp => new VmInspectionDriver(
            options.VmLink.BindAddress,
            options.VmLink.Port,
            sp.GetRequiredService<IInspectionWorkflow>(),
            sp.GetRequiredService<RecipeConfig>().Vm.Outputs,
            sp.GetRequiredService<ILogger<VmInspectionDriver>>(),
            sp.GetService<ILoggerFactory>()));

        return services;
    }

    /// <summary>注册 Devices 层：相机、触发源、设备管理器</summary>
    public static IServiceCollection AddDevices(this IServiceCollection services,
        AppPaths appPaths, AppOptions options)
    {
        services.AddSingleton<CameraFactory>(sp =>
            new CameraFactory(appPaths, options.Camera, sp.GetService<ILoggerFactory>()));

        services.AddSingleton<SwappableCamera>(sp =>
        {
            var factory = sp.GetRequiredService<CameraFactory>();
            return new SwappableCamera(factory.Create(),
            mode => factory.CreateByMode(mode),
            options.Camera.Mode);   // 初始模式必须显式传入：写死默认值会让 UI 的去重守卫误判
        });
        services.AddSingleton<ICamera>(sp => sp.GetRequiredService<SwappableCamera>());

        services.AddSingleton<ITriggerSource>(sp =>
        {
            var plc = options.Plc;
            if (!plc.Enabled)
                return new TimerTrigger(options.Trigger.TimerIntervalMs);

            // PLC 模式：连接是单例（触发源轮询、结果回写共用），归属组合根；
            // 触发源 Dispose 时不关连接（谁 new 谁管）。
            var conn = new ModbusTcpPlc(plc.Ip, plc.Port, plc.Station);
            return new PlcTriggerSource(
                conn,
                ModbusAddressParser.Parse(plc.TriggerAddress),
                ModbusAddressParser.Parse(plc.HeartbeatAddress),
                plc.PollIntervalMs,
                onError: ex => sp.GetService<ILogger<PlcTriggerSource>>()
                    ?.LogError(ex, "PLC 触发源轮询异常"));
        });

        if (options.Plc.Enabled)
        {
            services.AddSingleton(sp =>
            {
                var conn = sp.GetRequiredService<ITriggerSource>() is PlcTriggerSource p
                    ? p.ConnectedPlc
                    : throw new InvalidOperationException("PLC 已启用但触发源不是 PlcTriggerSource");
                return new PlcResultWriter(conn,
                    ModbusAddressParser.Parse(options.Plc.ResultAddress),
                    sp.GetService<ILogger<PlcResultWriter>>());
            });
        }

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
        AppPaths appPaths, AppOptions options)
    {
        // 不注册全进程单例 InspectionDbContext：
        // 管家清理线程与检测保存线程会并发用它，EF Core 禁止同一 context 并发操作。
        // Repository 现在每个操作自建/自释 context，所以注册为单例也安全。
        services.AddSingleton<IInspectionRepository>(sp =>
            new InspectionRepository(appPaths.DbPath, sp.GetService<ILogger<InspectionRepository>>()));

        // 审计走独立仓储而不是复用 IInspectionRepository：
        // 检测记录 30 天后由管家删，审计要按年留 —— 保留策略不同，将来清理规则一分就乱。
        // 库文件是同一个（备份一个文件就是完整证据），但表的生命周期各管各的。
        services.AddSingleton<IAuditRepository>(sp =>
            new AuditRepository(appPaths.DbPath, sp.GetService<ILogger<AuditRepository>>()));

        // 账号表与检测/审计同库。它是全库唯一会被 UPDATE 的表（改密、停用、记最后登录），
        // 另外两张是纯追加。
        services.AddSingleton<IUserRepository>(sp =>
            new UserRepository(appPaths.DbPath, sp.GetService<ILogger<UserRepository>>()));
        services.AddSingleton(sp =>
        {
            var s = new ImageStoreService(appPaths.StorageFolder);
            s.SetMode(options.Storage.Mode switch
            { "NGOnly" => ImageStoreMode.NGOnly, "Sampling" => ImageStoreMode.Sampling, _ => ImageStoreMode.All });
            return s;
        });

        return services;
    }

    /// <summary>注册 Service 层：健康检查、后台管家</summary>
    public static IServiceCollection AddAppServices(this IServiceCollection services,
        AppPaths appPaths, AppOptions options)
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
                recipe,
                appPaths.AssetRoot);
        });

        // 管家服务 2026-10-19 迁入 Service 层（VisionStation.Service.Housekeeping）——
        // 运维逻辑不住 UI 层。阈值在这里从 appsettings.json 解析，Service 层只收明确参数。
        services.AddSingleton<IHousekeepingService>(sp => new HousekeepingService(
            sp.GetRequiredService<IInspectionRepository>(),
            appPaths.StorageFolder,
            options.Housekeeping,
            sp.GetService<ILogger<HousekeepingService>>()));

        // 会话：当前账号、有效角色、5 分钟无操作降级、8 小时班次登出。
        //
        // 它不依赖审计 —— 会话变化走事件通知，由 App 层决定记什么。
        // 反过来依赖会成环（审计要读会话才知道"这条审计算谁的"）。
        services.AddSingleton<ISessionService>(_ => new SessionService());

        // 账号服务：认证（含失败锁定）、改密、出厂管理员种子。
        services.AddSingleton<IUserService>(sp => new UserService(
            sp.GetRequiredService<IUserRepository>(),
            sp.GetRequiredService<IAuditService>(),
            sp.GetService<ILogger<UserService>>()));

        // 审计服务：Record 只做一次 TryWrite（微秒级），落库在后台线程 ——
        // 调用点全在按钮命令里，不能被 SQLite 的 5 秒锁超时卡住。
        //
        // 身份改为读会话：登录之后每条审计都带真实操作人；
        // 未登录时回落到 Windows 账户并把角色记作「未登录」——
        // 启动阶段（AppStart）正是这个状态，它本身也是有效信息。
        services.AddSingleton<IAuditService>(sp =>
        {
            var session = sp.GetRequiredService<ISessionService>();
            return new AuditService(
                sp.GetRequiredService<IAuditRepository>(),
                identity: () => session.CurrentUser is { } user
                    ? (user.UserName, user.RoleText)
                    : (Environment.UserName, "未登录"),
                sp.GetService<ILogger<AuditService>>());
        });

        // 会话桥：把服务层的会话事件搬到 UI 线程，并补记超时类审计。
        // 注册只是让人能拿到它 —— 真正让它工作的是 AppHost 启动时解析一次（订阅事件）。
        services.AddSingleton<SessionBridge>();

        // 登录窗 VM 用 Transient：每次登录要一份干净的，上一次的错误提示不该带过来
        services.AddTransient<LoginViewModel>();

        // 弹登录窗的抽象。ViewModel 只认接口，所以"没权限该弹登录"能用替身测，
        // 不必在测试里创建 WPF 窗口。
        services.AddSingleton<ILoginPrompt>(sp => new WpfLoginPrompt(sp));

        // 检测链路（采图→检测→标注→存图→存库）从 VM 移到 App 服务层（S4a）。
        // 它不引用任何界面类型，只有位图转换会产出 WPF 的 BitmapSource —— 那属于呈现适配。
        services.AddSingleton<IInspectionWorkflow, InspectionWorkflow>();

        // 配方保存从 VM 移到 Service：VM 只表达“保存”，不碰路径、JSON 和临时文件。
        // S7 起是"配方目录"而不是单文件：目录里放多份配方，靠 .active 指针决定用哪份。
        services.AddSingleton<IRecipeStore>(sp => new JsonRecipeStore(
            appPaths.RecipeFolder,
            sp.GetService<ILogger<JsonRecipeStore>>()));

        return services;
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
        services.AddSingleton<TopBarViewModel>();

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
