using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 状态栏 ViewModel
///
/// 职责：
///   1. 订阅 CameraStatusMessage → 更新相机在线状态
///   2. 显示 PLC / 相机 / FPS / Cycle / OK / NG / CPU / 内存
///
/// 数据来源：
///   - CameraStatusMessage → CameraOnline
///   - OK/NG/FPS/Cycle 由 MainViewModel 直接赋值（性能考虑）
/// </summary>
public partial class StatusBarViewModel : ObservableObject
{
    /// <summary>运行状态文字</summary>
    [ObservableProperty]
    private string _statusText = "就绪";

    /// <summary>PLC 是否在线</summary>
    [ObservableProperty]
    private bool _plcOnline = true;

    /// <summary>相机是否在线</summary>
    [ObservableProperty]
    private bool _cameraOnline;

    /// <summary>每秒处理帧数（实时吞吐，EMA 平滑，如 "0.5"；无帧时为 "--"）</summary>
    [ObservableProperty]
    private string _fps = "--";

    /// <summary>单帧处理周期（毫秒）</summary>
    [ObservableProperty]
    private string _cycleTime = "--";

    /// <summary>累计 OK 数量</summary>
    [ObservableProperty]
    private int _okCount;

    /// <summary>累计 NG 数量</summary>
    [ObservableProperty]
    private int _ngCount;

    /// <summary>CPU 使用率</summary>
    [ObservableProperty]
    private string _cpuUsage = "--";

    /// <summary>内存占用</summary>
    [ObservableProperty]
    private string _memoryUsage = "--";

    /// <summary>图像源描述（FileCamera / Webcam / HikCamera）</summary>
    [ObservableProperty]
    private string _imageSource = "FileCamera";

    public StatusBarViewModel()
    {
        // 相机状态变化 → 更新在线指示
        WeakReferenceMessenger.Default.Register<StatusBarViewModel, CameraStatusMessage>(this,
            (vm, msg) => vm.CameraOnline = msg.Value);
    }
}
