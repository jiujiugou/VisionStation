using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisionStation.App.Abstractions;
using VisionStation.App.Composition;
using VisionStation.App.Composition.Lifecycle;
using VisionStation.App.Infrastructure;
using VisionStation.App.Services;
using VisionStation.App.Services.Inspection;
using VisionStation.App.Services.Interlock;
using VisionStation.App.Services.Imaging;
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
/// DI 扩展方法 —— 按模块分组注册，<c>AppHost.Configure</c> 一行调用一个。
///
/// <para>
/// 读这个文件只需要盯三件事：
/// <list type="number">
///   <item>每个类型注册成 Singleton 还是 Transient —— 也就是"它活多久"；</item>
///   <item>lambda 工厂里 <c>new</c> 出来的对象**谁负责 Dispose** ——
///         注册成 Singleton 的会被容器释放，工厂里自己 new 又没交给容器的要自己管；</item>
///   <item>哪些注册是"只装配、不干活" —— 端口、设备连接、订阅都发生在
///         <see cref="Composition.AppHost.InitializeAsync"/>，不在注册时。</item>
/// </list>
/// </para>
/// <para>
/// <b>这里不注册任何 View（Window / UserControl）</b>：视图由 <c>App.xaml</c> 的 DataTemplate
/// 按 VM 类型创建。容器管可视树的生命周期，等于把"谁关窗口"这件事交给 DI，是收不回来的。
/// </para>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册检测引擎（配方 + 引擎）。全系统只有这一处决定"用哪套检测"。
    ///
    /// 改造前这里造的是 Calibration + ToolFactory + Pipeline；
    /// 现在只造一个 <see cref="IInspectionEngine"/>，而且只有 VM 一条路 ——
    /// 判定逻辑在 VM 流程里，组合根不持有任何算法参数和模型路径。
    /// </summary>
    /// <param name="recipe">已加载的配方。引擎类型由它的 <c>engine</c> 字段决定，非法值直接抛。</param>
    public static IServiceCollection AddDetection(this IServiceCollection services,
        RecipeConfig recipe)
    {
        services.AddSingleton(recipe);

        services.AddSingleton<IInspectionEngine>(_ => InspectionEngineFactory.Create(recipe));

        return services;
    }

    /// <summary>
    /// 注册 VM 结果链路（TCP 中转）。
    ///
    /// 只在配方引擎是 Vm、且 appsettings 里 VmLink:Enabled=true 时才真正 Start() ——
    /// 见 <see cref="Composition.AppHost.InitializeAsync"/>。注册本身无副作用（不占端口）。
    /// </summary>
    /// <remarks>
    /// 三个参数都来自配置/配方：<c>BindAddress</c>、<c>Port</c>（这台机器）、
    /// <c>StallTimeoutSeconds</c>（这条产线）、<c>RecipeConfig.Vm.Outputs</c>（这个产品）。
    /// 其中断流阈值是业务参数，代码里没有默认值可猜，必须由配置给出。
    /// </remarks>
    public static IServiceCollection AddVmLink(this IServiceCollection services, AppOptions options)
    {
        services.AddSingleton(sp => new VmInspectionDriver(
            options.VmLink.BindAddress,
            options.VmLink.Port,
            sp.GetRequiredService<IInspectionWorkflow>(),
            sp.GetRequiredService<RecipeConfig>().Vm.Outputs,
            sp.GetRequiredService<ILogger<VmInspectionDriver>>(),
            TimeSpan.FromSeconds(options.VmLink.StallTimeoutSeconds),
            sp.GetService<ILoggerFactory>()));

        return services;
    }

    /// <summary>
    /// 注册 Devices 层：触发源、PLC 连接、设备管理器。
    ///
    /// <para>
    /// 三个判断都在这里做完，上层不再关心：
    /// PLC 开没开、走 TCP 还是 RTU、触发源该用 PLC 还是定时器。
    /// </para>
    /// </summary>
    /// <param name="appPaths">设备层当前不用，保留以统一签名（设备若要读资源目录会用到）</param>
    /// <param name="options">PLC 段与定时器间隔</param>
    public static IServiceCollection AddDevices(this IServiceCollection services,
        AppPaths appPaths, AppOptions options)
    {
        services.AddSingleton<ITriggerSource>(sp =>
        {
            var plc = options.Plc;
            if (!plc.Enabled)
                return new TimerTrigger(options.Trigger.TimerIntervalMs);

            // PLC 模式：连接是单例（触发源轮询、结果回写共用），归属组合根；
            // 触发源 Dispose 时不关连接（谁 new 谁管）。
            // 传输方式由配置选：Tcp 走网线/网关，Rtu 走串口（现场主流）。
            // 这是**唯一**决定用哪种传输的地方，上层两处消费者只认 IPlcConnection。
            IPlcConnection conn = plc.Transport switch
            {
                PlcTransport.Rtu => new ModbusRtuPlc(plc.SerialPort, plc.BaudRate, plc.Station,
                    plc.DataBits, plc.Parity, plc.StopBits, plc.SerialTimeoutMs),
                _ => new ModbusTcpPlc(plc.Ip, plc.Port, plc.Station),
            };
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
            var dm = new DeviceManager(sp.GetService<ILogger<DeviceManager>>());
            dm.Register(new TriggerDevice(sp.GetRequiredService<ITriggerSource>()));
            return dm;
        });

        return services;
    }

    /// <summary>
    /// 注册 Storage 层：三个仓储，共用一个 SQLite 文件。
    ///
    /// <para>
    /// 图片存储（<c>ImageStoreService</c>）**已于 2026-10-27 整体删除**，连同 <c>CameraFrame</c>
    /// 与其 7 条单测一起。原委：相机层删除后本程序不再写图 —— 图由 VM 存盘、
    /// 我们只按报文路径去读 —— 那个服务已经没有任何生产调用者，留着只会让人
    /// 以为"存图策略"还在生效（`Storage:Mode` 也是同一批删掉的）。
    /// </para>
    /// </summary>
    /// <param name="appPaths">只用来取 <c>DbPath</c>（已解析成绝对路径）</param>
    /// <param name="options">
    /// 当前实现未使用，保留是为了和其它 <c>Add*</c> 扩展方法保持一致的签名形状，
    /// 将来若把连接串/超时配置化，改动只落在这里而不必改调用点。
    /// </param>
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
        return services;
    }

    /// <summary>
    /// 注册 Service 层的服务：健康检查、后台管家、会话/账号/审计、工作流、配方存储。
    ///
    /// <para>
    /// 这里的顺序不是随便排的，几处 <c>new</c> 依赖前面的单例已经注册好
    /// （例如审计要读会话、账号服务要写审计）。改顺序前先看每个 lambda 里取了什么。
    /// </para>
    /// </summary>
    /// <param name="appPaths">管家要的图片目录、健康检查要的库路径与资源根</param>
    /// <param name="options">管家阈值</param>
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
                // 「存储路径」这项探的其实是"数据根可写"（写探针文件 → 删掉）——
                // 那个失败会连带毁掉日志、数据库和配方保存。图片目录已默认不配置
                // （图归 VM 存删，2026-10-27 F-31），所以回落成数据根本身，
                // 否则空路径会让这项自检永远报"无法创建目录"。
                appPaths.StorageFolder ?? appPaths.DataRoot,
                recipe,
                appPaths.AssetRoot,
                // F-49：第 4 项「运行时缺失」原本恒 null（这个参数没人传，等于那半个分支永远走不到）。
                // 现在传真实的探测结果：VM 是"推送驱动"的引擎，它的运行时就是结果推送链路的入口，
                // 入口立不起来 = 这一趟跑下来永远不会有一条结果。探测失败返回原因字符串，正常返回 null。
                engineRuntimeProblem: ProbeEngineRuntime(recipe, options));
        });

        // 管家服务 2026-10-19 迁入 Service 层（VisionStation.Service.Housekeeping）——
        // 运维逻辑不住 UI 层。阈值在这里从 appsettings.json 解析，Service 层只收明确参数。
        // 图片目录可能为 null（默认就是）：那表示上位机不代管图片，图由 VisionMaster 存删。
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

        // 图像来源设置：界面「图像来源」窗写它，检测链路每帧读它。
        // 单独一份 JSON 而不是塞进 appsettings —— appsettings 随程序发布、装完就不该动；
        // 这份是"这台机器的现场设置"，换机器本来就该是另一份。
        services.AddSingleton(sp => new ImageSourceSettingsStore(
            appPaths.ImageSourceSettingsPath,
            sp.GetService<ILogger<ImageSourceSettingsStore>>()));

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
    /// 第 4 项「运行时缺失」的探测（F-49）：VM 推送链路的入口能不能立起来。
    ///
    /// <para>
    /// <b>为什么探的不是"本机装没装 VisionMaster"</b>：本程序不加载任何海康 SDK
    /// （见 <c>VmExternalEngine</c> 与《引擎集成-说明》），检测跑在 VM 进程里、结果用 TCP 推过来。
    /// 所以"本机没装 VM"对这条进程不构成故障 —— 照着那个说法造一条探测，只会得到
    /// 一条永远绿的假检查（本项目原则：假绿灯比没有更糟）。
    /// 这个进程真正的运行时前置是**推送入口**：链路没开、或端口占不住，
    /// 就永远收不到一条结果，而界面上只会表现为"一直没东西"。
    /// </para>
    ///
    /// <para>
    /// 两条失败路径各自对应的现场问题：
    /// <list type="bullet">
    ///   <item><c>VmLink:Enabled=false</c> 且配方引擎是 Vm —— 监听根本不会起
    ///         （<c>AppHost.InitializeAsync</c> 只在启用时才 Start），本次运行零结果。
    ///         这种反方向的错配原本**连一句警告都没有**（只记了"启用了但引擎不是 Vm"那一种）。</item>
    ///   <item>入口地址/端口占不住 —— 多半是上一个实例没退干净，或别的软件占了端口。
    ///         <c>VmInspectionDriver.Start()</c> 也会抛，但那是自检之后的事；
    ///         提前一屏报出来，现场不用翻日志才知道是端口问题。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>绑定方式与真实监听保持一致</b>（<c>IPAddress.Parse</c> + <c>TcpListener</c>，
    /// 不额外设 <c>ExclusiveAddressUse</c>），否则探测结论会和随后 <c>VmResultListener.Start()</c>
    /// 的真实行为对不上：探到"能绑"却绑不上是误绿，反过来是误红。
    /// 试绑完立刻释放；释放到真正 Start 之间的窗口里 VM 若正好来连会失败一次 ——
    /// VM 侧本来就是重连设计，可接受。
    /// </para>
    ///
    /// <para>
    /// 异常一律翻成一句人话返回，绝不外抛：这段跑在 DI 工厂里，抛出去会让 <c>IHealthChecker</c>
    /// 整个解析失败，自检一条结果都拿不到。
    /// </para>
    /// </summary>
    /// <returns>运行时不可用的原因；null = 正常</returns>
    private static string? ProbeEngineRuntime(RecipeConfig recipe, AppOptions options)
    {
        // 只有 Vm 这一条引擎路线（写别的引擎名在建引擎时就抛，根本走不到自检）。
        if (!string.Equals(recipe.Engine, EngineNames.Vm, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!options.VmLink.Enabled)
            return "结果推送链路未启用（VmLink:Enabled=false），本次运行收不到任何检测结果";

        var address = options.VmLink.BindAddress;
        var port = options.VmLink.Port;
        TcpListener? probe = null;
        try
        {
            probe = new TcpListener(IPAddress.Parse(address), port);
            probe.Start();
            return null;
        }
        catch (Exception ex)
        {
            return $"结果推送入口 {address}:{port} 无法监听（{ex.GetType().Name}: {ex.Message}）";
        }
        finally
        {
            try { probe?.Stop(); } catch { /* 释放失败不改变结论 */ }
        }
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
        // 唯一允许弹 MessageBox 的地方。VM 通过 IDialogService 表达"要确认"，不直接碰 WPF。
        services.AddSingleton<IDialogService, WpfDialogService>();
        services.AddSingleton<SidebarViewModel>();
        services.AddSingleton<ImagePanelViewModel>();
        services.AddSingleton<ResultPanelViewModel>();
        services.AddSingleton<LogPanelViewModel>();

        // 启动自检结果快照（F-49 跑、F-47 联锁读）：AppHost 启动时 Update，控制面板构造时注入。
        services.AddSingleton<HealthSnapshot>();

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

        services.AddSingleton<ImageSourcePageViewModel>();

        // 审计页：只有管理员能从侧栏进入（NavItem.RequiredPermission = ViewAudit）。
        // 注册为单例是为了让导航服务能直接拿到它，翻页时保留上次的过滤条件与结果。
        services.AddSingleton<AuditPageViewModel>();

        // 历史页：操作员也能进（Permission.ViewHistory）。
        // 它不是"工程师后台"，是操作员发现 NG 后自己回看那一件的地方。
        services.AddSingleton<HistoryPageViewModel>();

        // 配方页：技术员及以上（Permission.SwitchRecipe）。只换激活配方 + 提示重启生效，
        // 不做运行时热重载（引擎与工作流都吃单例 RecipeConfig，换配方要重建它们）。
        services.AddSingleton<RecipePageViewModel>();

        // 用户页：仅管理员（Permission.ManageUsers）。
        services.AddSingleton<UserManagePageViewModel>();

        // 导航：路由表在这里组装（检测 / 图像来源 / 历史 / 审计 / 配方 / 用户）。
        // NavigationService 只认一张表，不查容器、不建工厂；缺页会在构造时直接抛。
        services.AddSingleton<INavigationService>(sp => new NavigationService(
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [NavigationService.InspectionRoute] = sp.GetRequiredService<InspectionPageViewModel>(),
                [NavigationService.ImageSourceRoute] = sp.GetRequiredService<ImageSourcePageViewModel>(),
                [NavigationService.HistoryRoute] = sp.GetRequiredService<HistoryPageViewModel>(),
                [NavigationService.AuditRoute] = sp.GetRequiredService<AuditPageViewModel>(),
                [NavigationService.RecipeRoute] = sp.GetRequiredService<RecipePageViewModel>(),
                [NavigationService.UsersRoute] = sp.GetRequiredService<UserManagePageViewModel>(),
            },
            sp.GetRequiredService<ILogger<NavigationService>>()));
        services.AddSingleton<ShellViewModel>();

        return services;
    }
}
