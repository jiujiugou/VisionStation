using CommunityToolkit.Mvvm.Messaging.Messages;
using VisionStation.App.Services.Inspection;

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
/// ROI 已更新消息
/// 发送者：RoiEditorVM（保存后）→ 订阅者：InspectionWorkflow（重新加载 ROIs）
/// </summary>
public sealed class RoisUpdatedMessage : ValueChangedMessage<bool>
{
    public RoisUpdatedMessage() : base(true) { }
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
