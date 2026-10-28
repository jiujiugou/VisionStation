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
using VisionStation.App.Services.Inspection;
using VisionStation.App.Services.Interlock;
using VisionStation.App.ViewModels;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Service;
using VisionStation.Service.Audit;
using VisionStation.Service.Housekeeping;
using VisionStation.Service.Recipes;
using VisionStation.Service.Security;
using VisionStation.Storage;

namespace VisionStation.App.Composition;

/// <summary>
/// 组合根与生命周期宿主：把"启动"和"关闭"这两个只能发生一次的过程，
/// 写死成两条有序的直线，中间没有一个可注入的编排器。
///
/// <para>
/// <b>一次进程只走这三步</b>：
/// <list type="number">
///   <item><see cref="Configure"/> —— 读配置、建容器、加载配方。纯装配，不碰硬件、不建表。</item>
///   <item><see cref="InitializeAsync"/> —— 连设备、自检、建库、补统计、起 VM 监听、记启动审计。</item>
///   <item><see cref="Show"/> —— 创建窗口并显示。</item>
/// </list>
/// 关闭走 <see cref="StopAsync"/> 的六步，顺序不能颠倒（见该方法注释）。
/// </para>
///
/// <para>
/// <b>为什么把顺序写死在类里，而不是做成"启动步骤列表"</b>：WPF 应用只有一个启动入口、
/// 一个关闭入口。把六步拆成可替换的管道，唯一的受益者是测试用的 mock，
/// 而代价是每次排查"到底卡在第几步"都要多跳两层。这里每一步都留一行带编号的日志，
/// 日志本身就是验证手段。
/// </para>
///
/// <para>
/// <b>线程归属</b>：<see cref="Configure"/> 与 <see cref="InitializeAsync"/> 的入口在 UI 线程
/// （WinUI 启动流程），<see cref="StopAsync"/> 也在 UI 线程被调用，但它内部把重活丢给线程池。
/// 三个字段 <c>_stopped</c> / <c>_closing</c> / <c>_services</c> 的并发访问都由
/// <see cref="Interlocked"/> 守住，不靠"反正只有 UI 线程会改"的假设。
/// </para>
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    /// <summary>配方 JSON 的序列化选项。忽略大小写：现场手改配方时大小写很难保证一致。</summary>
    private static readonly JsonSerializerOptions RecipeJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>测试注入的配置源；生产为 null，走 appsettings.json。</summary>
    private readonly IConfiguration? _configuration;

    /// <summary>测试注入的额外注册（比如替换掉真实仓储）；生产为 null。</summary>
    private readonly Action<IServiceCollection>? _configureServices;

    /// <summary>创建窗口的委托。生产 new MainWindow；测试给假窗口。</summary>
    private readonly Func<IServiceProvider, IAppWindow> _windowFactory;

    /// <summary>关闭收尾完成后通知看门狗"这是正常退出"。启动没走完时由 App 侧自行忽略。</summary>
    private readonly Action? _onCleanExit;

    /// <summary>请求进程退出。生产是 <c>Application.Shutdown()</c>；测试传一个记录调用的委托。</summary>
    private readonly Action _requestShutdown;

    /// <summary>关闭预算：在途帧 3s + 容器释放 4s 的余量。超时不是错误，只是要如实记成 timeout。</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);

    /// <summary>容器。Configure 之后才非空；StopAsync 会把它释放掉（释放后不再置空，幂等靠 _stopped）。</summary>
    private ServiceProvider? _services;

    /// <summary>路径解析结果。Configure 时建，早于容器 —— 日志目录都要靠它。</summary>
    private AppPaths? _paths;

    /// <summary>主窗口。Show 之后才非空；关闭流程不碰它（关窗由 WPF + Shutdown 收尾）。</summary>
    private IAppWindow? _window;

    /// <summary>1 = StopAsync 已经开始过。用 Interlocked 而不是 bool 字段：重复关窗会并发进来。</summary>
    private int _stopped;

    /// <summary>1 = 本次关闭已被接管。见 <see cref="HandleWindowClosingAsync"/> 的 C6 契约。</summary>
    private int _closing;

    /// <summary>无参构造：全部依赖走默认（appsettings.json + 真实窗口）。测试通常用 internal 那个。</summary>
    public AppHost()
        : this(null, null, null)
    {
    }

    /// <summary>生产入口：看门狗信号由 App 决定（启动失败不该发 clean）。</summary>
    public AppHost(Action? onCleanExit)
        : this(null, null, null, onCleanExit)
    {
    }

    /// <summary>
    /// 测试/内部入口：所有外部依赖都可替换。
    /// <paramref name="onCleanExit"/> 与 <paramref name="requestShutdown"/> 分开，
    /// 是因为它们语义不同 —— 前者告诉看门狗"正常退出"，后者真的结束进程；
    /// 启动失败时应该只做后者。
    /// </summary>
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

    /// <summary>
    /// 服务容器。**只在 Configure 之后可用**，未初始化时抛而不是返回 null ——
    /// 拿到 null 的地方会写 <c>?.</c>，然后静默什么都不做，那是最难查的一类空引用。
    /// </summary>
    public IServiceProvider Services => _services
        ?? throw new InvalidOperationException("AppHost 尚未 Configure");

    /// <summary>路径解析结果。同样在 Configure 之后可用。</summary>
    public AppPaths Paths => _paths
        ?? throw new InvalidOperationException("AppHost 尚未 Configure");

    /// <summary>
    /// 本次启动的自检结果（原始数据）。界面不直接用它 —— 显示文案由
    /// <see cref="HealthSummary.Describe"/> 生成，失败项已经在 InitializeAsync 里逐条记了日志。
    /// </summary>
    public IReadOnlyList<HealthResult> HealthResults { get; private set; } = Array.Empty<HealthResult>();

    /// <summary>
    /// 第一步：装配。读配置 → 校验 → 建日志 → 建容器 → 加载配方 → 注册所有依赖。
    ///
    /// <para>
    /// 这里**只做装配**：不连设备、不建数据库、不占端口。原因是失败要分层 ——
    /// "配置写错了"和"PLC 连不上"是完全不同的两件事，混在一次调用里就分不清。
    /// </para>
    /// <para>
    /// 重复调用会直接返回（<c>_services != null</c>），不是覆盖重建 ——
    /// 重建会让旧容器里的后台线程变成孤儿。
    /// </para>
    /// </summary>
    /// <param name="dataRootOverride">
    /// 可写数据根的显式声明（启动参数 <c>--data-root=...</c>），空 = exe 目录（便携模式，W-15）。
    /// </param>
    public void Configure(string? dataRootOverride = null)
    {
        if (_services != null) return;

        var config = _configuration ?? new ConfigurationBuilder()
            // reloadOnChange 关闭（W-14）：没有任何消费者，开着只制造"改了配置怎么没生效"的错觉。
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        // S5：绑定 + 校验的唯一入口。校验失败这里直接抛 OptionsValidationException，
        // 由 App.StartAsync 写 crash.log 并以非 0 退出 —— 不静默回落默认值。
        var options = AppOptionsBinder.Bind(config, dataRootOverride);

        var appPaths = new AppPaths(options);
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

        var recipe = LoadRecipe(appPaths);

        services
            .AddSingleton(appPaths)
            .AddSingleton(options)
            .AddDetection(recipe)
            .AddVmLink(options)
            .AddDevices(appPaths, options)
            .AddStorage(appPaths, options)
            .AddAppServices(appPaths, options)
            .AddViews();

        _configureServices?.Invoke(services);

        _services = services.BuildServiceProvider();
    }

    /// <summary>
    /// 第二步：把设备与数据拉起来。<b>顺序是这张清单的全部价值</b>：
    ///
    /// <list type="number">
    ///   <item>连设备 —— 必须早于自检，因为自检读的是 <c>DeviceManager.AllConnected</c>；</item>
    ///   <item>自检 —— 失败只记日志，**不弹模态框**（会阻塞启动线程）；</item>
    ///   <item>提前解析检测页 VM —— 它构造函数里订阅触发信号，不解析就没人订阅；</item>
    ///   <item>建表 —— 必须早于起 VM 监听，否则第一帧结果来了没地方写；</item>
    ///   <item>补今日良率基数 —— 重启后良率不该归零；读失败只记日志（宁可数字偏小）；</item>
    ///   <item>初始化账号 —— 失败只记日志（最坏是没人能登录，不是起不来）；</item>
    ///   <item>解析会话桥 —— 不解析就没人订阅会话事件；</item>
    ///   <item>起 VM 监听 —— 配方引擎不是 Vm 时只记警告不起端口；</item>
    ///   <item>记启动审计 —— 放最后，记下的才是"启动成功"而不是"开始尝试启动"。</item>
    /// </list>
    ///
    /// <para>
    /// 第 5~7 步统一用 try/catch 包住并只记 Error：这三件事失败都应该降级运行，
    /// 而不是让产线连状态都看不见。**能降级的失败不要升级成启动失败**。
    /// </para>
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var services = _services
            ?? throw new InvalidOperationException("AppHost 尚未 Configure");

        var log = services.GetRequiredService<ILogger<App>>();

        // S5（W-15）：旧数据不迁移（用户拍板：全部删除，从空开始）。
        // 数据根改为 exe 目录后，旧 cwd 里的测试数据不再被读取，也不需要兼容分支。

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

        // 快照给启动联锁（F-47）用：控制面板运行期读它，不重跑自检
        services.GetRequiredService<HealthSnapshot>().Update(results);

        services.GetRequiredService<IHousekeepingService>();

        // 检测页 VM 靠构造函数订阅触发信号 —— 没人解析它，就没人采图。
        // 它由导航服务在壳体构造时才被创建，而壳体是在 Show() 里才解析的；
        // 这里提前取一次，让"订阅触发"发生在自检之后、开窗之前，顺序可预期。
        services.GetRequiredService<InspectionPageViewModel>();

        await services.GetRequiredService<IInspectionRepository>().EnsureCreatedAsync();

        // 良率与产量从库里补基数 —— 重启后不该归零。
        //
        // 口径是"今天 0 点到现在"（自然日）。三班倒跨 0 点的现场要改这里，
        // 那属于业务参数，不在代码里替现场拍板。
        // 失败只记日志：最坏结果是这一屏从本次运行重新计数（数字偏小），
        // 而不是产线起不来 —— 宁可少一个数，也不要看不见界面。
        try
        {
            var (okToday, ngToday) = await services.GetRequiredService<IInspectionRepository>()
                .GetStatisticsAsync(DateTime.Today);
            services.GetRequiredService<ResultPanelViewModel>().Seed(okToday, ngToday);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[统计] 读取今日良率失败，良率将从本次运行重新计数");
        }

        // 账号表为空时建出厂管理员（首次登录强制改密）。
        //
        // 失败只记日志、不中断启动：账号初始化失败的正确后果是"没人能登录、只能看状态"，
        // 而不是"程序起不来、产线连状态都看不见"。前者可降级，后者不可。
        try
        {
            await services.GetRequiredService<IUserService>().EnsureDefaultAdminAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[用户] 初始化账号失败：本次启动将无法登录，只能查看状态");
        }

        // 会话桥必须在这里解析一次：它靠订阅会话事件工作，不解析就没人订阅，
        // 5 分钟无操作降级、8 小时班次登出都不会通知到界面。
        // 同样不阻断启动 —— 最坏结果是"权限过期了界面没提示"。
        try
        {
            services.GetRequiredService<SessionBridge>();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[会话] 初始化会话桥失败：权限自动回收不会通知界面");
        }

        // VM 模式（TCP 中转）：VisionMaster 主动把结果推过来，这里起监听。
        // 顺序放在数据库建表之后 —— 第一帧结果就要写库，表还没建就收结果只会报错。
        var loadedRecipe = services.GetRequiredService<RecipeConfig>();
        var appOptions = services.GetRequiredService<AppOptions>();
        if (appOptions.VmLink.Enabled)
        {
            if (string.Equals(loadedRecipe.Engine, EngineNames.Vm, StringComparison.OrdinalIgnoreCase))
            {
                var vmDriver = services.GetRequiredService<VmInspectionDriver>();
                vmDriver.Start();
                log.LogInformation("[VM链路] 已启动，监听 {Endpoint}，等待 VisionMaster 推送", vmDriver.Endpoint);
            }
            else
            {
                log.LogWarning("VmLink:Enabled=true，但配方引擎是 {Engine}（不是 Vm），VM 结果监听未启动",
                    loadedRecipe.Engine);
            }
        }

        // 审计流的第一条：证明「这个库是从这次启动开始记的」。
        // 放在初始化最末 —— 前面的自检、建表、监听都已经完成，记下来的是一条"启动成功"，
        // 而不是"开始尝试启动"。两者在排障时完全是不同的信息。
        services.GetRequiredService<IAuditService>().Record(
            AuditAction.AppStart,
            target: loadedRecipe.Name,
            detail: $"引擎={loadedRecipe.Engine}；VmLink={(appOptions.VmLink.Enabled ? "开" : "关")}");
    }

    /// <summary>
    /// 第三步：创建并显示主窗口。
    ///
    /// <para>
    /// 幂等（<c>_window != null</c> 直接返回）：重复调用只会在任务栏里多出第二个窗口，
    /// 而它同样挂着 Closing 处理器，关闭时会走两遍收尾。
    /// </para>
    /// <para>
    /// 窗口是从 <see cref="_windowFactory"/> 来的，不是从容器解析的 ——
    /// 窗口不属于容器（容器只管 VM 与后台服务），这一点见 IAppWindow 的说明。
    /// </para>
    /// </summary>
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

        // 1.5 停 VM 结果监听。必须早于第 4 步释放容器：
        // 否则 VM 再推一帧进来，处理时会落到已释放的 DbContext 上。
        Guarded("1.5 stop-vm-link", () => services.GetService<VmInspectionDriver>()?.Stop(), budget);

        // 2 应用级取消：页面 VM 据此拒绝排队中的触发，在途帧拿到的令牌开始取消。
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

        // 3.5 把界面日志队列灌进面板。日志面板是合批刷新的（BeginInvoke），
        // 不在这里刷一次，关闭期间那几条提示会随窗口一起消失 —— 文件日志里有，界面上看不到。
        // 必须早于第 4 步：容器释放之后再去碰 VM 就是在跟"已释放对象"赛跑。
        try
        {
            services.GetService<LogPanelViewModel>()?.FlushPending();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[关闭] 3.5 界面日志刷新失败");
        }

        // 3.8 审计收尾：先记 AppStop，再把队列里剩下的写完。
        //
        // 必须在第 4 步之前 —— 容器一释放，审计队列和仓储就都没了。
        // 先 Record 后 Flush 的顺序也不能反：反了这条 AppStop 自己就被漏在队列里。
        try
        {
            var audit = services.GetService<IAuditService>();
            if (audit != null)
            {
                audit.Record(AuditAction.AppStop, target: "",
                    detail: $"正常关闭（累计 {budget.ElapsedMilliseconds}ms）");

                var auditBudget = timeout - budget.Elapsed;
                audit.Flush(auditBudget > TimeSpan.Zero ? auditBudget : TimeSpan.FromSeconds(1));
                Log.Information("[关闭] 3.8 审计收尾 OK（{Elapsed}ms）", budget.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[关闭] 3.8 审计收尾失败");
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

    /// <summary>
    /// 加载当前配方（S7）。
    ///
    /// 顺序：`.active` 指针指向的文件 → 配置里的 Recipe:Path → 内嵌默认配方。
    /// 指针优先是"换型"的落地点：现场切了配方，下次启动就该装新的那份，
    /// 而不是每次动 appsettings（那要 rebuild，等于没得切）。
    /// </summary>
    private static RecipeConfig LoadRecipe(AppPaths paths)
    {
        var activePath = ResolveActiveRecipePath(paths);
        var candidates = activePath is null
            ? new[] { paths.RecipePath }
            : new[] { activePath, paths.RecipePath };

        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;

                var json = File.ReadAllText(path);

                // 判据与 JsonRecipeStore 一致：能说出自己用哪个引擎的才是配方
                if (RecipeConfig.HasEngineDeclaration(json))
                {
                    var recipe = JsonSerializer.Deserialize<RecipeConfig>(json, RecipeJsonOptions);
                    if (recipe is not null)
                    {
                        Log.Information("已加载配方 {Name}（引擎 {Engine}，{Path}）",
                            recipe.Name, recipe.Engine, path);
                        return recipe;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "配方加载失败 ({Path})", path);
            }
        }

        Log.Warning("配方加载失败（缺失/损坏/为空），回退到内嵌默认配方。候选: {Candidates}",
            string.Join(" → ", candidates));
        return LoadEmbeddedRecipe();
    }

    /// <summary>读 .active 指针，返回指针指向的配方绝对路径；没有指针/指向的文件不存在时返回 null。</summary>
    private static string? ResolveActiveRecipePath(AppPaths paths)
    {
        var pointer = Path.Combine(paths.RecipeFolder, JsonRecipeStore.ActivePointerFile);
        try
        {
            if (!File.Exists(pointer)) return null;

            var name = File.ReadAllText(pointer).Trim();
            if (string.IsNullOrEmpty(name)) return null;

            var path = Path.Combine(paths.RecipeFolder, Path.GetFileName(name));
            if (File.Exists(path)) return path;

            Log.Warning("配方指针指向的文件不存在：{Name}（回退到默认配方）", name);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取配方指针失败，回退到默认配方");
            return null;
        }
    }

    private static RecipeConfig LoadEmbeddedRecipe()
    {
        const string resourceName = "VisionStation.App.Config.recipes.default.json";
        using var stream = typeof(AppHost).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"内嵌配方资源缺失: {resourceName}（打包配置被改动过？）");
        using var reader = new StreamReader(stream);
        var recipe = JsonSerializer.Deserialize<RecipeConfig>(reader.ReadToEnd(), RecipeJsonOptions);
        if (recipe == null || string.IsNullOrWhiteSpace(recipe.Engine))
            throw new InvalidOperationException("内嵌默认配方内容为空或无效（缺 engine 段）");
        return recipe;
    }
}
