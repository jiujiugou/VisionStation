using CommunityToolkit.Mvvm.Messaging.Messages;
using VisionStation.App.Services.Inspection;
using VisionStation.Core.Users;
using VisionStation.Devices.Vm;
using VisionStation.Service.Security;

namespace VisionStation.App;

// ═══════════════════════════════════════════════════════════════
//  消息定义 —— ViewModel 之间的唯一通信方式
//
//  原则：ViewModel 之间的**通知**走消息（谁关心谁订阅，收发双方互不知道）。
//       但"当前页是谁"不是通知，是状态 —— 那条走 INavigationService，别再发消息。
//
//  注意：页面切换曾经是 NavigateMessage + MainWindow 改 Visibility，
//       结果是窗口必须认识所有页面。S3b 已把它换成导航服务 + DataTemplate。
//
//  用法：
//    发：WeakReferenceMessenger.Default.Send(new XxxMessage(data));
//    收：WeakReferenceMessenger.Default.Register<MyVM, XxxMessage>(this, (vm, msg) => { ... });
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 检测完成消息 —— 一帧的全部产出：报告、两张已冻结的位图、耗时。
///
/// 发送者：InspectionPageViewModel（工作流返回后、在 UI 线程上发一次）
/// 订阅者：ImagePanelVM、ResultPanelVM、LogPanelVM、StatusBarVM
///
/// 为什么把图也塞进消息，而不是继续让编排方直接写面板属性：
/// 检测跑在后台线程，而 WeakReferenceMessenger 是**同步投递**的 ——
/// 在哪个线程 Send，订阅者就在哪个线程收。只要 Send 这一次发生在 UI 线程上，
/// 所有面板的集合操作就是安全的，不需要每个面板自己再切一次线程。
/// 消息里带的位图已经 Freeze，跨线程传递不会触发"该对象归另一个线程所有"。
/// </summary>
public sealed class InspectionResultMessage : ValueChangedMessage<InspectionRunResult>
{
    public InspectionResultMessage(InspectionRunResult result) : base(result) { }
}

/// <summary>
/// 触发状态变化消息
/// 发送者：ControlPanelVM（自动模式切换 / 停止 / 急停时发出）
/// 订阅者：SidebarVM（true=暗化，false=亮起）
/// </summary>
public sealed class TriggerStateMessage : ValueChangedMessage<bool>
{
    public TriggerStateMessage(bool isRunning) : base(isRunning) { }
}

/// <summary>
/// 日志消息
/// 发送者：任何 ViewModel 都可以发
/// 订阅者：LogPanelVM（显示在界面上）、文件日志（Serilog）
/// </summary>
public sealed class LogMessage : ValueChangedMessage<(string Text, LogMsgLevel Level)>
{
    public LogMessage(string text, LogMsgLevel level = LogMsgLevel.Info)
        : base((text, level)) { }
}

/// <summary>
/// 相机状态变化消息
/// 发送者：ControlPanelVM（重连时）、InspectionPageViewModel（每帧检测后）
/// 订阅者：StatusBarVM
/// </summary>
public sealed class CameraStatusMessage : ValueChangedMessage<bool>
{
    public CameraStatusMessage(bool isAvailable) : base(isAvailable) { }
}

/// <summary>
/// 运行/工程师模式切换消息
/// 发送者：TopBarVM（顶栏的模式按钮）
/// 订阅者：SidebarVM（运行模式隐藏工程师专属页面）
///
/// 为什么模式走消息而不是让侧边栏读顶栏的属性：
/// 侧边栏和顶栏是同级的两个常驻区域，谁都不拥有谁。让侧边栏去引用顶栏，
/// 等于把"兄弟"变成"父子"，以后顶栏一改侧边栏就编译不过。
/// </summary>
public sealed class EngineerModeMessage : ValueChangedMessage<bool>
{
    public EngineerModeMessage(bool isEngineerMode) : base(isEngineerMode) { }
}

/// <summary>
/// 会话变化消息 —— 登录 / 登出 / 权限降级。
///
/// 发送者：SessionBridge（App 层）
/// 订阅者：TopBarVM、SidebarVM、以及任何要按角色放开/收紧控件的地方
///
/// 为什么要经过一个桥：会话服务可能在定时器线程上触发变化（5 分钟无操作降级、
/// 8 小时班次登出），ViewModel 直接订阅那个事件就是跨线程改绑定属性 ——
/// 轻则界面不刷新，重则抛"该对象归另一个线程所有"。
/// SessionBridge 负责切回 UI 线程，再同步投递这条消息，订阅方就不必自己关心线程。
/// </summary>
public sealed record SessionChangedMessage(
    UserAccount? User,
    UserRole EffectiveRole,
    SessionChangeKind Kind,
    string? Reason);

/// <summary>
/// VM 链路状态 —— 界面显示用的一份快照。
///
/// 为什么带宽窄的原始数据（State/时刻/阈值）而不是让界面自己算：
/// 界面拿不到"监听器最后收到结果的时间"，那是设备层的字段；
/// 而且"多久算断流"是配置，必须在驱动层统一，不能在几个界面各取一套。
/// </summary>
/// <param name="State">四态：未监听 / VM 没连上 / 正常 / 断流</param>
/// <param name="Endpoint">监听地址，断线时操作员要照着它去核对 VM 里的 IP 端口</param>
/// <param name="LastLineLocal">最后一条结果的本机时间；null = 本次启动以来从未收到</param>
/// <param name="SinceLast">距最后一条结果多久；从未收到时为 null</param>
/// <param name="StallTimeout">断流阈值（文案里要说清"多少秒算断"，否则 N 秒是个没依据的数）</param>
/// <param name="IsRunning">工位是否在运行中。停机时没结果不升级为告警，否则告警会变背景噪音</param>
public sealed record VmLinkStatus(
    VmLinkState State,
    string Endpoint,
    DateTime? LastLineLocal,
    TimeSpan? SinceLast,
    TimeSpan StallTimeout,
    bool IsRunning);

/// <summary>
/// VM 链路状态变化消息
/// 发送者：VmInspectionDriver（每秒检查，**只在状态翻面时发**，不随秒刷）
/// 订阅者：ImagePanelVM（断流态文案）
/// </summary>
public sealed class VmLinkStatusMessage : ValueChangedMessage<VmLinkStatus>
{
    public VmLinkStatusMessage(VmLinkStatus status) : base(status) { }
}

/// <summary>
/// 日志级别（全局共用）
/// </summary>
public enum LogMsgLevel
{
    /// <summary>普通信息（不显示在告警面板）</summary>
    Info,
    /// <summary>成功（绿色，正常流程完成）</summary>
    Success,
    /// <summary>警告（黄色，需要留意但不影响运行）</summary>
    Warning,
    /// <summary>错误（红色，需要立即处理）</summary>
    Error
}
