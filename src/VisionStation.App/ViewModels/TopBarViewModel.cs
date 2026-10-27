using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.Abstractions;
using VisionStation.Core.Models;
using VisionStation.Core.Users;
using VisionStation.Devices;
using VisionStation.Service.Audit;
using VisionStation.Service.Security;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 顶栏 ViewModel —— 工位名、当前配方、设备状态灯、当前用户、运行/工程师模式。
///
/// 为什么要有顶栏（2026-10-24）：
///   改造前"当前是哪份配方、用的哪个引擎、相机在不在"这些信息散在控制面板和状态栏里，
///   操作员要低头看底部才能确认。产线软件的惯例是把这三样钉在顶部常驻 ——
///   状态永远可见，是交互的第一条铁律（见 Cognex VisionView 的 HMI 形态）。
///
/// 配方为什么是只读的：
///   这里显示 <see cref="RecipeConfig"/> 的 Name / Engine / 画面尺寸，**不提供下拉切换**。
///   切换配方要换引擎实例（<see cref="VisionStation.Core.Engines.IInspectionEngine"/>）、
///   重载 .sol、重建配方绑定的 ROI —— 那是"换型"流程，不是顶栏点一下就完的事。
///
/// 用户区为什么写"未登录"而不是启动就弹登录：
///   启动弹模态框，无人值守时进程会卡在对话框上，看门狗的 WaitForExit 永远等不到退出信号。
///   所以未登录是合法状态：能看状态、不能动参数。要动手时才登录。
/// </summary>
public partial class TopBarViewModel : ObservableObject
{
    /// <summary>触发源。只用来判断"是不是 PLC"并读它的连接状态（<see cref="RefreshPlc"/>）。</summary>
    private readonly ITriggerSource _trigger;

    /// <summary>审计。记三件事：模式切换、登出、以及被拒的模式切换。</summary>
    private readonly IAuditService _audit;

    /// <summary>会话。本类是界面侧唯一读"当前账号 / 有效角色 / 权限"的地方。</summary>
    private readonly ISessionService _session;

    /// <summary>弹登录窗。放抽象而不是直接 new 窗口，好让"没权限该弹登录"能被测。</summary>
    private readonly ILoginPrompt _loginPrompt;

    /// <summary>工位名。取自窗口标题的同一套语义，不放配置里 —— 现场改名字改这一个常量即可。</summary>
    public string StationName { get; } = "金属零件视觉检测工位";

    /// <summary>当前配方名（来自 <see cref="RecipeConfig.Name"/>）</summary>
    public string RecipeName { get; }

    /// <summary>
    /// 当前引擎。运行时只可能是 <c>Vm</c>（ONNX 已于 2026-10-27 整条线删除），
    /// 仍然读配方里的原值显示，便于和配方文件对照。
    /// </summary>
    public string EngineText { get; }

    /// <summary>配方声明的画面尺寸；未声明时如实说明，不显示 0×0</summary>
    public string FrameText { get; }

    /// <summary>图像链路在线（订阅 <see cref="ImageLinkStatusMessage"/>）：平台有没有在推结果</summary>
    [ObservableProperty]
    private bool _imageLinkOnline;

    /// <summary>PLC 在线。仅当触发源是 PLC 时才有意义。</summary>
    [ObservableProperty]
    private bool _plcOnline;

    /// <summary>PLC 状态文字（在线 / 离线 / 未接入）</summary>
    [ObservableProperty]
    private string _plcText = "PLC 未接入";

    /// <summary>是否工程师模式</summary>
    [ObservableProperty]
    private bool _isEngineerMode;

    /// <summary>是否已登录</summary>
    [ObservableProperty]
    private bool _isAuthenticated;

    /// <summary>当前用户显示文字："张三 · 工程师" 或 "未登录"</summary>
    [ObservableProperty]
    private string _userText = "未登录";

    /// <summary>模式显示文字</summary>
    public string ModeText => IsEngineerMode ? "工程师模式" : "运行模式";

    /// <summary>模式按钮的文字随状态变（操作员看得懂"我要干什么"）</summary>
    public string ModeButtonText => IsEngineerMode ? "退出工程师模式" : "切换模式";

    /// <summary>登录按钮文字：已登录时是"切换用户"</summary>
    public string LoginButtonText => IsAuthenticated ? "切换用户" : "登录";

    /// <summary>
    /// 构造：从配方取三个只读显示项，订阅三路消息，并用会话当前状态初始化用户区。
    ///
    /// <para>
    /// 最后那次 <see cref="ApplySession"/> 是刻意的：窗口可能在"已登录"之后才被创建
    /// （登录窗先于主窗弹出的场景），那时不会有 <see cref="SessionChangedMessage"/> 补发，
    /// 只能自己去问一次。假定"未登录"会让顶栏和实际权限对不上。
    /// </para>
    /// </summary>
    public TopBarViewModel(
        RecipeConfig recipe,
        ITriggerSource trigger,
        IAuditService audit,
        ISessionService session,
        ILoginPrompt loginPrompt)
    {
        _trigger = trigger;
        _audit = audit;
        _session = session;
        _loginPrompt = loginPrompt;

        RecipeName = string.IsNullOrWhiteSpace(recipe.Name) ? "未命名配方" : recipe.Name;
        EngineText = string.IsNullOrWhiteSpace(recipe.Engine) ? "未指定引擎" : recipe.Engine;
        FrameText = recipe.Sensor.IsDeclared
            ? $"{recipe.Sensor.Width}×{recipe.Sensor.Height}"
            : "尺寸未声明";

        WeakReferenceMessenger.Default.Register<TopBarViewModel, ImageLinkStatusMessage>(this,
            static (vm, msg) => vm.ImageLinkOnline = msg.Value);

        // 每帧检测完顺手刷一次 PLC 灯：PLC 的连接状态只在收发时才会变，
        // 定时器去轮询它反而是多一次网络 IO。
        WeakReferenceMessenger.Default.Register<TopBarViewModel, InspectionResultMessage>(this,
            static (vm, _) => vm.RefreshPlc());

        // 会话变化：刷新用户区；降级/登出导致失去工程师权限时顺带退出工程师模式
        WeakReferenceMessenger.Default.Register<TopBarViewModel, SessionChangedMessage>(this,
            static (vm, msg) => vm.ApplySession(msg));

        // 初始状态从会话里读，而不是假定"未登录"：窗口可能是在已登录后才创建的
        ApplySession(new SessionChangedMessage(
            session.CurrentUser, session.EffectiveRole, SessionChangeKind.Login, null));

        RefreshPlc();
    }

    /// <summary>
    /// 会话变化后刷新用户区，并在权限不够时强制退出工程师模式。
    ///
    /// <para>
    /// 后半段是这个方法存在的核心理由：**权限收回了，界面必须跟着收回**。
    /// 只改文字不改可见性，就是"能看见就能点"的经典漏洞 ——
    /// 超时降级之后，侧边栏还挂着工程师入口、控制面板还全开着。
    /// </para>
    /// </summary>
    private void ApplySession(SessionChangedMessage msg)
    {
        IsAuthenticated = _session.IsAuthenticated;

        var user = _session.CurrentUser;
        UserText = user is null
            ? "未登录"
            : $"{user.EffectiveDisplayName} · {RolePermissions.Describe(_session.EffectiveRole)}";

        // 降级或登出后权限不够了：必须把工程师模式一起退掉。
        // 不退的话侧栏还挂着「图像来源」入口、控制面板维护区还全开着 ——
        // 权限收回了但界面没收回，这正是"能看见就能点"的经典漏洞。
        if (IsEngineerMode && !_session.HasPermission(Permission.EnterEngineerMode))
            IsEngineerMode = false;

        if (msg.Kind is SessionChangeKind.Downgraded or SessionChangeKind.Logout
            && !string.IsNullOrWhiteSpace(msg.Reason))
        {
            WeakReferenceMessenger.Default.Send(new LogMessage(msg.Reason, LogMsgLevel.Warning));
        }
    }

    /// <summary>
    /// 模式变化的唯一出口：刷新文案 → 广播 <see cref="EngineerModeMessage"/> → 写审计。
    ///
    /// <para>
    /// 审计放在这里而不是 <c>ToggleMode</c> 里，是因为**自动退出**（权限降级）也走这条路径。
    /// 埋在命令里会漏掉"被系统退掉"这一半，而那一半恰恰是排障时最想看的。
    /// </para>
    /// </summary>
    partial void OnIsEngineerModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(ModeButtonText));
        WeakReferenceMessenger.Default.Send(new EngineerModeMessage(value));

        // 埋点放在这里而不是 ToggleMode 里：这是模式真正变化的唯一入口，
        // 降级/登出导致的自动退出也会走这里，一样能被记到。
        _audit.Record(AuditAction.ModeSwitch,
            target: RecipeName,
            before: value ? "运行模式" : "工程师模式",
            after: value ? "工程师模式" : "运行模式");
    }

    /// <summary>登录状态变化 → 刷新"登录 / 切换用户"按钮文字。</summary>
    partial void OnIsAuthenticatedChanged(bool value)
        => OnPropertyChanged(nameof(LoginButtonText));

    /// <summary>
    /// 切换运行/工程师模式。
    ///
    /// 进工程师模式要权限，出工程师模式不要 —— 权限系统只该拦"往上走"。
    /// 没权限先弹登录；登录后权限仍不够就不切并说明原因（比如用操作员登录了）。
    /// </summary>
    [RelayCommand]
    private void ToggleMode()
    {
        if (!IsEngineerMode && !_session.HasPermission(Permission.EnterEngineerMode))
        {
            _audit.Record(AuditAction.ModeSwitch, RecipeName,
                result: AuditResult.Denied,
                detail: $"当前账号无权进入工程师模式（需要{RolePermissions.Describe(UserRole.Engineer)}）");

            if (!_loginPrompt.ShowLogin("切换工程师模式需要工程师权限，请登录"))
                return;

            if (!_session.HasPermission(Permission.EnterEngineerMode))
            {
                var role = _session.CurrentUser is null
                    ? "未登录"
                    : RolePermissions.Describe(_session.EffectiveRole);

                WeakReferenceMessenger.Default.Send(new LogMessage(
                    $"当前身份（{role}）无权进入工程师模式，需要工程师或管理员",
                    LogMsgLevel.Warning));
                return;
            }
        }

        IsEngineerMode = !IsEngineerMode;
    }

    /// <summary>登录 / 切换用户</summary>
    [RelayCommand]
    private void Login()
    {
        if (!_loginPrompt.ShowLogin(null)) return;

        // 切换到了权限更低的账号：把工程师模式退掉，否则界面权限与账号不一致
        if (IsEngineerMode && !_session.HasPermission(Permission.EnterEngineerMode))
            IsEngineerMode = false;
    }

    /// <summary>退出登录</summary>
    [RelayCommand]
    private void Logout()
    {
        var user = _session.CurrentUser;
        if (user is null) return;

        // 主动登出的审计在这里记（SessionBridge 只管自动超时那种，靠 IsAutomatic 区分）
        _audit.RecordAs(user.UserName, user.RoleText, AuditAction.Logout, "主动退出",
            detail: "用户点击顶栏「退出」");

        _session.Logout("主动退出");
    }

    /// <summary>
    /// 刷新 PLC 灯。用 <c>is PlcTriggerSource</c> 判断而不是再注入一个 PLC 连接：
    /// 触发源就是"这台机器有没有 PLC"的唯一真相，多一个依赖就多一处可能不一致。
    ///
    /// <para>
    /// 调用时机是"每帧检测后"，不是定时器：PLC 连接状态只在收发时才会变，
    /// 空闲时轮询它纯粹是多一次网络 IO。
    /// </para>
    /// <para>
    /// 顺带广播 <see cref="PlcStatusMessage"/>，让状态栏那个 PLC 灯跟着变 ——
    /// 否则它只是个永远灰着的装饰（这是真实发生过的：那个属性从来没有赋值点）。
    /// 判断只在这里做一次，其余地方都是显示。
    /// </para>
    /// </summary>
    private void RefreshPlc()
    {
        if (_trigger is PlcTriggerSource plc)
        {
            PlcOnline = plc.IsConnected;
            PlcText = plc.IsConnected ? "PLC 在线" : "PLC 离线";
        }
        else
        {
            PlcOnline = false;
            PlcText = "PLC 未接入";
        }

        WeakReferenceMessenger.Default.Send(new PlcStatusMessage(PlcOnline, PlcText));
    }
}
