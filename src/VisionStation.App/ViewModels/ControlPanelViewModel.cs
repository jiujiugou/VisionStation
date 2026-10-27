using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.Abstractions;
using VisionStation.Core.Engines;
using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Service.Audit;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 控制面板 ViewModel —— 检测页底部那一排按钮的全部命令。
///
/// <para>
/// <b>职责</b>：
/// <list type="number">
///   <item>直接操控 <see cref="ITriggerSource"/>（开始/停止/自动/急停），不经任何中间 VM 转发；</item>
///   <item>状态变化通过 Messenger 广播（<see cref="TriggerStateMessage"/>、<see cref="LogMessage"/>）；</item>
///   <item>凡是"改了设备行为"的动作都写一条审计（启动/停止/急停）。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>依赖</b>：<see cref="ITriggerSource"/> 有三种实现（手动 / 定时 / PLC 上升沿，
/// 由组合根按配置选），本类只认接口，不关心是哪一种。
/// </para>
///
/// <para>
/// <b>为什么这里没有"重载流程"和"扫描输出"两个按钮了</b>（2026-10-27 删除）：
/// 两个都建立在 VM SDK 上，而 SDK 已随相机层一起移除（改走 TCP 收结果）。
/// "重载"的唯一实现 <c>VmExternalEngine.Reload()</c> 恒返回 null，界面却报"引擎已重载"——
/// 一次假成功；"扫描"的 <c>IOutputScanner</c> 更是零实现，按钮点了只会提示不可用。
/// 流程归 VM 客户端管，改完在 VM 里重跑，本程序没有可重载、可扫描的资源。
/// 留着按钮比没有按钮更容易让人以为程序坏了。
/// </para>
///
/// <para>
/// <b>它不订阅任何业务消息</b>：它是命令的起点，不需要听别人。
/// </para>
/// </summary>
public partial class ControlPanelViewModel : ObservableObject
{
    // ── 依赖（由 DI 注入）──
    private readonly ITriggerSource _trigger;
    private readonly IDialogService _dialog;

    /// <summary>配方。只为审计里的配方名读它（"这班是谁在用哪份配方"）。</summary>
    private readonly RecipeConfig _recipe;

    /// <summary>审计。只记"改变设备/判定行为"的动作，不记按钮点击。</summary>
    private readonly IAuditService _audit;

    /// <summary>
    /// 审计对象名。用配方名而不是空字符串 —— 事后要能回答"这班是谁在用哪份配方"，
    /// 只记"操作员按了启动"等于什么都没记。
    /// </summary>
    private string RecipeName => string.IsNullOrWhiteSpace(_recipe.Name) ? "未命名配方" : _recipe.Name;

    /// <summary>是否处于自动模式（按钮文字和颜色跟着变）</summary>
    [ObservableProperty]
    private bool _isAutoMode;

    /// <summary>自动模式按钮上显示的文字</summary>
    [ObservableProperty]
    private string _autoButtonText = "自动模式";

    /// <summary>IsAutoMode 变化 → 自动更新按钮文字</summary>
    partial void OnIsAutoModeChanged(bool value)
    {
        AutoButtonText = value ? "自动运行中" : "自动模式";
    }

    public ControlPanelViewModel(
        ITriggerSource trigger,
        IDialogService dialog,
        RecipeConfig recipe,
        IAuditService audit)
    {
        _trigger = trigger;
        _dialog = dialog;
        _recipe = recipe;
        _audit = audit;
    }

    // ═══════════════════════════════════════════════════════════
    //  命令 —— 每个按钮一个方法，直接操控硬件，发消息通知其他 VM
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 手动触发一次检测 —— <b>本程序不发起检测</b>（2026-10-27 F-10 拍板）。
    ///
    /// <para>
    /// 相机与判定都归 VisionMaster：图在平台侧产生、结果由平台主动推送，本程序没有可触发的采图动作。
    /// 所以这里只说明原因然后返回 —— 这个按钮在产线上是给人按的，点了没反应会被当成程序卡死。
    /// </para>
    /// <para>
    /// <b>为什么不按引擎类型判断</b>：运行时只有一个引擎（VM），"按能力分流"没有第二种取值可对照；
    /// 原来的 <c>IPushDrivenEngine</c> 标记只在这一处产生提示，真正的执行路径压根不看它 ——
    /// 结论对、理由不对，所以标记已删除，这里改成直接说事实。
    /// </para>
    /// </summary>
    [RelayCommand]
    private void StartDetection()
    {
        WeakReferenceMessenger.Default.Send(new LogMessage(
            "检测由 VisionMaster 执行：本程序不采图、不发起检测，等待平台推送结果",
            LogMsgLevel.Warning));
    }

    /// <summary>
    /// 停止检测（完成当前帧后停止）
    /// 关闭自动运行、发消息通知侧边栏恢复亮色、记录日志
    /// </summary>
    [RelayCommand]
    private void Stop()
    {
        _trigger.Stop();
        IsAutoMode = false;
        WeakReferenceMessenger.Default.Send(new TriggerStateMessage(false));
        WeakReferenceMessenger.Default.Send(new LogMessage("手动停止", LogMsgLevel.Warning));
        _audit.Record(AuditAction.Stop, RecipeName, detail: "手动停止");
    }

    /// <summary>
    /// 切换自动 / 手动模式
    /// 自动模式：Start 触发源 → 发消息暗化侧边栏
    /// 手动模式：Stop 触发源 → 发消息恢复侧边栏
    /// </summary>
    [RelayCommand]
    private void ToggleAutoMode()
    {
        IsAutoMode = !IsAutoMode;
        if (IsAutoMode)
        {
            _trigger.Start();
            WeakReferenceMessenger.Default.Send(new TriggerStateMessage(true));
            _audit.Record(AuditAction.Start, RecipeName, detail: "进入自动运行");
        }
        else
        {
            _trigger.Stop();
            WeakReferenceMessenger.Default.Send(new TriggerStateMessage(false));
            _audit.Record(AuditAction.Stop, RecipeName, detail: "退出自动运行");
        }
    }

    /// <summary>
    /// 急停 —— 弹出确认框后立即制动
    /// 和"停止"的区别：不等待当前帧处理完，直接停
    /// </summary>
    [RelayCommand]
    private async Task EmergencyStopAsync()
    {
        if (!await _dialog.ConfirmAsync("急停", "确认急停？"))
            return;

        _trigger.Stop();
        IsAutoMode = false;
        WeakReferenceMessenger.Default.Send(new TriggerStateMessage(false));
        WeakReferenceMessenger.Default.Send(new LogMessage("急停已触发", LogMsgLevel.Error));
        _audit.Record(AuditAction.EStopPressed, RecipeName,
            detail: "急停触发：触发源已停止，自动模式已退出");
    }

    /// <summary>
    /// 清空当前显示和统计数据 —— 交接班时用。
    ///
    /// <para>
    /// 只发一条 <see cref="ClearAllMessage"/>，具体清什么由各面板自己定：
    /// 结果面板清当前结论与累计计数、日志面板清滚动列表、图像面板清当前画面。
    /// 这样控制面板不必认识三个面板的类型，新增一块面板只要自己订阅即可。
    /// </para>
    /// <para>
    /// <b>它不动任何数据</b>：库里的记录一条都不删，良率的今日基数也不重置。
    /// 清的是"眼前看到的东西"，不是账。要删记录走历史页的保留期，那是另一条路。
    /// </para>
    /// </summary>
    [RelayCommand]
    private void ClearAll()
    {
        WeakReferenceMessenger.Default.Send(new ClearAllMessage());
        WeakReferenceMessenger.Default.Send(new LogMessage("显示已清空（记录不受影响）", LogMsgLevel.Info));
    }
}
