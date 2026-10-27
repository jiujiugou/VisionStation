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
    private readonly ITriggerSource _trigger;
    private readonly IAuditService _audit;
    private readonly ISessionService _session;
    private readonly ILoginPrompt _loginPrompt;

    /// <summary>工位名。取自窗口标题的同一套语义，不放配置里 —— 现场改名字改这一个常量即可。</summary>
    public string StationName { get; } = "金属零件视觉检测工位";

    /// <summary>当前配方名（来自 <see cref="RecipeConfig.Name"/>）</summary>
    public string RecipeName { get; }

    /// <summary>当前引擎（Vm / Onnx）</summary>
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

    private void ApplySession(SessionChangedMessage msg)
    {
        IsAuthenticated = _session.IsAuthenticated;

        var user = _session.CurrentUser;
        UserText = user is null
            ? "未登录"
            : $"{user.EffectiveDisplayName} · {RolePermissions.Describe(_session.EffectiveRole)}";

        // 降级或登出后权限不够了：必须把工程师模式一起退掉。
        // 不退的话侧栏还挂着相机页、控制面板还全开着 —— 权限收回了但界面没收回，
        // 这正是"能看见就能点"的经典漏洞。
        if (IsEngineerMode && !_session.HasPermission(Permission.EnterEngineerMode))
            IsEngineerMode = false;

        if (msg.Kind is SessionChangeKind.Downgraded or SessionChangeKind.Logout
            && !string.IsNullOrWhiteSpace(msg.Reason))
        {
            WeakReferenceMessenger.Default.Send(new LogMessage(msg.Reason, LogMsgLevel.Warning));
        }
    }

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
    }
}
