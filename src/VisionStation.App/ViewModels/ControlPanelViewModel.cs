using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.Devices;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 控制面板 ViewModel
///
/// 职责：
///   1. 直接操控 ITriggerSource（开始/停止/自动/急停）和 ICamera（重连）
///   2. 不再通过 MainViewModel 中转——按钮点击 → 直接调硬件
///   3. 状态变化通过 Messenger 广播（TriggerState、CameraStatus、Log）
///
/// 依赖：
///   - ITriggerSource：触发信号源（Manual/Auto/将来PLC）
///   - ICamera：相机（File/Webcam/将来HikCamera）
///
/// 这个 ViewModel 不订阅任何消息——它是命令的起点，不需要听别人的。
/// </summary>
public partial class ControlPanelViewModel : ObservableObject
{
    // ── 硬件依赖（由 DI 注入）──
    private readonly ITriggerSource _trigger;
    private readonly ICamera _camera;

    /// <summary>是否处于自动模式（按钮文字和颜色跟着变）</summary>
    [ObservableProperty]
    private bool _isAutoMode;

    /// <summary>自动模式按钮上显示的文字</summary>
    [ObservableProperty]
    private string _autoButtonText = "自动模式";

    /// <summary>相机是否在线（控制绿点颜色）</summary>
    [ObservableProperty]
    private bool _cameraOnline;

    /// <summary>已采集帧数（界面显示用）</summary>
    [ObservableProperty]
    private int _frameCount;

    /// <summary>IsAutoMode 变化 → 自动更新按钮文字</summary>
    partial void OnIsAutoModeChanged(bool value)
    {
        AutoButtonText = value ? "自动运行中" : "自动模式";
    }

    /// <summary>
    /// 构造函数（由 DI 注入 Trigger 和 Camera）
    /// </summary>
    public ControlPanelViewModel(ITriggerSource trigger, ICamera camera)
    {
        _trigger = trigger;
        _camera = camera;
        CameraOnline = _camera.IsAvailable; // 初始化：检查相机是否可用
    }

    // ═══════════════════════════════════════════════════════════
    //  命令 —— 每个按钮一个方法，直接操控硬件，发消息通知其他 VM
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 手动触发一次检测
    /// 流程：按钮点击 → 如果是 ManualTrigger → 调 Fire() → Triggered 事件 → MainViewModel.OnTriggered
    /// </summary>
    [RelayCommand]
    private void StartDetection()
    {
        _trigger.Fire();
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
        }
        else
        {
            _trigger.Stop();
            WeakReferenceMessenger.Default.Send(new TriggerStateMessage(false));
        }
    }

    /// <summary>
    /// 急停 —— 弹出确认框后立即制动
    /// 和"停止"的区别：不等待当前帧处理完，直接停
    /// </summary>
    [RelayCommand]
    private void EmergencyStop()
    {
        if (System.Windows.MessageBox.Show("确认急停？", "急停",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
            == System.Windows.MessageBoxResult.Yes)
        {
            _trigger.Stop();
            IsAutoMode = false;
            WeakReferenceMessenger.Default.Send(new TriggerStateMessage(false));
            WeakReferenceMessenger.Default.Send(new LogMessage("急停已触发", LogMsgLevel.Error));
        }
    }

    /// <summary>
    /// 重新连接相机
    /// 当前实现：FileCamera 重新扫描文件夹
    /// 将来：HikCamera 重新枚举设备 + 打开
    /// </summary>
    [RelayCommand]
    private void Reconnect()
    {
        if (_camera is FileCamera fc) fc.RefreshFileList();
        CameraOnline = _camera.IsAvailable;
        WeakReferenceMessenger.Default.Send(new CameraStatusMessage(_camera.IsAvailable));
        WeakReferenceMessenger.Default.Send(new LogMessage(
            _camera.IsAvailable ? "相机已就绪" : "相机无图像",
            _camera.IsAvailable ? LogMsgLevel.Success : LogMsgLevel.Error));
    }

    /// <summary>
    /// 清空当前显示和统计数据
    /// 注意：这里只发消息，具体的清空操作由各子 VM 自己处理
    /// </summary>
    [RelayCommand]
    private void ClearAll()
    {
        WeakReferenceMessenger.Default.Send(new LogMessage("已清空", LogMsgLevel.Info));
    }

    /// <summary>
    /// 保存当前标注图像到本地文件
    /// 保存逻辑在 MainViewModel.SaveImage 里实现
    /// </summary>
    [RelayCommand]
    private void SaveImage()
    {
        WeakReferenceMessenger.Default.Send(new LogMessage("保存图片", LogMsgLevel.Info));
    }
}
