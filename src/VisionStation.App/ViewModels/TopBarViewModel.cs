using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Service.Audit;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 顶栏 ViewModel —— 工位名、当前配方、设备状态灯、运行/工程师模式。
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
///   先摆一个能真的下拉、但下拉后引擎没换的控件，比不做更糟。
///
/// PLC 状态为什么可能显示"未接入"：
///   只有触发源是 <see cref="PlcTriggerSource"/> 时才有真实连接可报。
///   用定时器或手动触发时，PLC 根本没参与，写"在线"就是假灯。
/// </summary>
public partial class TopBarViewModel : ObservableObject
{
    private readonly ITriggerSource _trigger;
    private readonly IAuditService _audit;

    /// <summary>工位名。取自窗口标题的同一套语义，不放配置里 —— 现场改名字改这一个常量即可。</summary>
    public string StationName { get; } = "金属零件视觉检测工位";

    /// <summary>当前配方名（来自 <see cref="RecipeConfig.Name"/>）</summary>
    public string RecipeName { get; }

    /// <summary>当前引擎（Vm / Onnx）</summary>
    public string EngineText { get; }

    /// <summary>配方声明的画面尺寸；未声明时如实说明，不显示 0×0</summary>
    public string FrameText { get; }

    /// <summary>相机在线（订阅 <see cref="CameraStatusMessage"/>）</summary>
    [ObservableProperty]
    private bool _cameraOnline;

    /// <summary>PLC 在线。仅当触发源是 PLC 时才有意义。</summary>
    [ObservableProperty]
    private bool _plcOnline;

    /// <summary>PLC 状态文字（在线 / 离线 / 未接入）</summary>
    [ObservableProperty]
    private string _plcText = "PLC 未接入";

    /// <summary>是否工程师模式</summary>
    [ObservableProperty]
    private bool _isEngineerMode;

    /// <summary>模式显示文字</summary>
    public string ModeText => IsEngineerMode ? "工程师模式" : "运行模式";

    public TopBarViewModel(RecipeConfig recipe, ITriggerSource trigger, IAuditService audit)
    {
        _trigger = trigger;
        _audit = audit;

        RecipeName = string.IsNullOrWhiteSpace(recipe.Name) ? "未命名配方" : recipe.Name;
        EngineText = string.IsNullOrWhiteSpace(recipe.Engine) ? "未指定引擎" : recipe.Engine;
        FrameText = recipe.Sensor.IsDeclared
            ? $"{recipe.Sensor.Width}×{recipe.Sensor.Height}"
            : "尺寸未声明";

        WeakReferenceMessenger.Default.Register<TopBarViewModel, CameraStatusMessage>(this,
            static (vm, msg) => vm.CameraOnline = msg.Value);

        // 每帧检测完顺手刷一次 PLC 灯：PLC 的连接状态只在收发时才会变，
        // 定时器去轮询它反而是多一次网络 IO。
        WeakReferenceMessenger.Default.Register<TopBarViewModel, InspectionResultMessage>(this,
            static (vm, _) => vm.RefreshPlc());

        RefreshPlc();
    }

    partial void OnIsEngineerModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ModeText));
        WeakReferenceMessenger.Default.Send(new EngineerModeMessage(value));

        // 埋点放在这里而不是 ToggleMode 里：这是模式真正变化的唯一入口，
        // 将来（阶段 2）登录后自动降级/登出也会走这个属性，一样能被记到。
        _audit.Record(AuditAction.ModeSwitch,
            target: RecipeName,
            before: value ? "运行模式" : "工程师模式",
            after: value ? "工程师模式" : "运行模式");
    }

    /// <summary>
    /// 切换运行/工程师模式。
    ///
    /// 当前没有口令 —— 口令要配合"配方锁定 + 权限位"整套才成立，
    /// 单独加一个弹框只是在演戏。等权限模块落地后再在这里拦。
    /// </summary>
    [RelayCommand]
    private void ToggleMode() => IsEngineerMode = !IsEngineerMode;

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
