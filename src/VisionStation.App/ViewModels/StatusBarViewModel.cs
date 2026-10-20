using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.Services.Inspection;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 状态栏 ViewModel
///
/// 职责：
///   1. 订阅 CameraStatusMessage → 更新相机在线状态
///   2. 显示 PLC / 相机 / FPS / Cycle / OK / NG / CPU / 内存
///
/// 数据来源：两条消息。
///   - <see cref="CameraStatusMessage"/> → <see cref="CameraOnline"/>
///   - <see cref="InspectionResultMessage"/> → 累计 OK/NG、单帧耗时、FPS
///
/// 累计值归本类自己维护（而不是由编排方算好塞进来）：它们是"显示统计"，
/// 谁显示谁记账，编排方不需要知道状态栏有哪几个数字。
/// </summary>
public partial class StatusBarViewModel : ObservableObject
{
    /// <summary>运行状态文字</summary>
    [ObservableProperty]
    private string _statusText = "就绪";

    /// <summary>
    /// PLC 是否在线。
    ///
    /// 默认必须是 false：当前项目里没有任何 PLC 通讯实现（零 Modbus/TCP、零串口），
    /// 默认写 true 等于在界面上挂一个假状态灯 —— 操作员会以为产线联动了，
    /// 而实际上没有任何数据通道。将来接上 PLC 后由通讯层真正回写这个值。
    /// </summary>
    [ObservableProperty]
    private bool _plcOnline;

    /// <summary>
    /// 相机是否在线。
    /// 构造时由 MainViewModel 用真实连接状态初始化，之后靠 CameraStatusMessage 更新。
    /// 默认 false 只代表"还没确认"，不代表离线 —— 所以启动时必须立刻赋真值，
    /// 否则相机明明连上了，状态栏会一直显示灰点，直到第一次检测跑完。
    /// </summary>
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

    /// <summary>单帧耗时的指数滑动平均，用来平滑 FPS（瞬时值抖动太大，看不出产线节拍）。</summary>
    private double _emaCycleMs;

    public StatusBarViewModel()
    {
        // 相机状态变化 → 更新在线指示
        WeakReferenceMessenger.Default.Register<StatusBarViewModel, CameraStatusMessage>(this,
            static (vm, msg) => vm.CameraOnline = msg.Value);

        // 一帧完成 → 记账 + 刷新节拍。消息在 UI 线程上到达，不需要再切线程。
        WeakReferenceMessenger.Default.Register<StatusBarViewModel, InspectionResultMessage>(this,
            static (vm, msg) =>
            {
                var result = msg.Value;

                if (result.Report.IsOk) vm.OkCount++;
                else vm.NgCount++;

                vm.CycleTime = $"{result.CycleMs} ms";
                vm._emaCycleMs = vm._emaCycleMs == 0
                    ? result.CycleMs
                    : vm._emaCycleMs * 0.7 + result.CycleMs * 0.3;

                // CycleMs 为 0（极快的一帧或本地缓存命中）时不做除法，直接显示 "--"，
                // 否则会显示成 Infinity，看着像故障。
                vm.Fps = vm._emaCycleMs > 0 ? $"{1000.0 / vm._emaCycleMs:0.0}" : "--";
            });
    }
}
