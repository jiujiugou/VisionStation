using CommunityToolkit.Mvvm.Messaging.Messages;
using VisionStation.Core.Models;

namespace VisionStation.App;

// ═══════════════════════════════════════════════════════════════
//  消息定义 —— ViewModel 之间的唯一通信方式
//
//  原则：任何 ViewModel 都不直接引用另一个 ViewModel。
//       想通信 → 发消息。关心的 → 订阅消息。
//       谁发的、谁收的，互相不知道。
//
//  用法：
//    发：WeakReferenceMessenger.Default.Send(new XxxMessage(data));
//    收：WeakReferenceMessenger.Default.Register<MyVM, XxxMessage>(this, (vm, msg) => { ... });
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 检测完成消息
/// 发送者：MainViewModel（Pipeline 跑完一帧后发出）
/// 订阅者：ImagePanelVM、ResultPanelVM、LogPanelVM、StatusBarVM
/// </summary>
public sealed class InspectionResultMessage : ValueChangedMessage<InspectionReport>
{
    public InspectionResultMessage(InspectionReport report) : base(report) { }
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
/// 发送者：ControlPanelVM（重连时）、MainViewModel（每帧检测后）
/// 订阅者：StatusBarVM
/// </summary>
public sealed class CameraStatusMessage : ValueChangedMessage<bool>
{
    public CameraStatusMessage(bool isAvailable) : base(isAvailable) { }
}

/// <summary>
/// ROI 已更新消息
/// 发送者：RoiEditorVM（保存后）→ 订阅者：MainViewModel（重新加载 ROIs）
/// </summary>
public sealed class RoisUpdatedMessage : ValueChangedMessage<bool>
{
    public RoisUpdatedMessage() : base(true) { }
}

/// <summary>
/// 页面导航消息
/// 发送者：SidebarVM → 订阅者：MainWindow（切换页面）
/// </summary>
public sealed class NavigateMessage : ValueChangedMessage<string>
{
    public NavigateMessage(string page) : base(page) { }
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
