using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.Abstractions;
using VisionStation.App.Services;
using VisionStation.Core.Engines;
using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Service.Audit;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 控制面板 ViewModel
///
/// 职责：
///   1. 直接操控 ITriggerSource（开始/停止/自动/急停）、ICamera（重连）、IInspectionEngine（重载/扫描）
///   2. 不再通过 MainViewModel 中转——按钮点击 → 直接调硬件
///   3. 状态变化通过 Messenger 广播（TriggerState、CameraStatus、Log）
///
/// 依赖：
///   - ITriggerSource：触发信号源（Manual/Auto/将来PLC）
///   - ICamera：相机（File/Webcam/将来HikCamera）
///   - IInspectionEngine：引擎（调完 .sol 后重载、扫描输出）
///
/// 这个 ViewModel 不订阅任何消息——它是命令的起点，不需要听别人的。
/// </summary>
public partial class ControlPanelViewModel : ObservableObject
{
    // ── 依赖（由 DI 注入）──
    private readonly ITriggerSource _trigger;
    private readonly ICamera _camera;
    private readonly IDialogService _dialog;
    private readonly IInspectionEngine _engine;
    private readonly AppPaths _paths;
    private readonly RecipeConfig _recipe;
    private readonly IAuditService _audit;

    /// <summary>
    /// 审计对象名。用配方名而不是空字符串 —— 事后要能回答"这班是谁在用哪份配方"，
    /// 只记"操作员按了启动"等于什么都没记。
    /// </summary>
    private string RecipeName => string.IsNullOrWhiteSpace(_recipe.Name) ? "未命名配方" : _recipe.Name;

    /// <summary>扫描结果序列化选项（缓存复用：每次 new 会被 CA1869 判为热路径浪费）</summary>
    private static readonly JsonSerializerOptions ScanJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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
    public ControlPanelViewModel(
        ITriggerSource trigger,
        ICamera camera,
        IDialogService dialog,
        IInspectionEngine engine,
        AppPaths paths,
        RecipeConfig recipe,
        IAuditService audit)
    {
        _trigger = trigger;
        _camera = camera;
        _dialog = dialog;
        _engine = engine;
        _paths = paths;
        _recipe = recipe;
        _audit = audit;
        CameraOnline = _camera.IsAvailable; // 初始化：检查相机是否可用
    }

    // ═══════════════════════════════════════════════════════════
    //  命令 —— 每个按钮一个方法，直接操控硬件，发消息通知其他 VM
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 手动触发一次检测
    /// 流程：按钮点击 → 如果是 ManualTrigger → 调 Fire() → Triggered 事件 → InspectionPageViewModel（S4 后不再经 MainViewModel 中转）
    /// </summary>
    [RelayCommand]
    private void StartDetection()
    {
        // VM 模式（推驱动）：检测由 VisionMaster 自己跑，本程序的触发源推不动它。
        // 这里必须明说，否则点了没反应会被当成程序卡死 —— 这个按钮在产线上是要给人按的。
        if (_engine is IPushDrivenEngine)
        {
            WeakReferenceMessenger.Default.Send(new LogMessage(
                $"当前引擎是 {_engine.Name}（推驱动）：检测由 VisionMaster 执行，本按钮不发起检测",
                LogMsgLevel.Warning));
            return;
        }

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
    /// 重新连接相机
    /// 当前实现：ICamera.Open() 转发给当前相机；FileCamera 重扫目录，HikCamera 重新枚举/打开
    /// </summary>
    [RelayCommand]
    private void Reconnect()
    {
        // 必须走 ICamera 契约：DI 注入的是 SwappableCamera，`is FileCamera` 永远为 false。
        // SwappableCamera.Open() 会转发给当前实现，FileCamera 会重扫目录。
        _camera.Open();
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
    /// 重新加载引擎资源 —— 在 VM 客户端改完 `.sol` 存盘后点这个，不用关程序。
    ///
    /// 先停触发再重载：重载期间进来的帧在引擎里会拿到 NG（实现自己保证），
    /// 但让"正在自动运行"的状态跟着变，操作员才知道发生了什么。
    /// </summary>
    [RelayCommand]
    private void ReloadEngine()
    {
        _trigger.Stop();
        IsAutoMode = false;
        WeakReferenceMessenger.Default.Send(new TriggerStateMessage(false));

        var problem = _engine.Reload();

        // 引擎重载是"改了判定逻辑"的动作，必须留痕：换完 .sol 前后结论漂了，
        // 得能查出是哪一分钟换的、换的是哪个引擎。
        _audit.Record(AuditAction.EngineReload, _engine.Name,
            result: problem is null ? AuditResult.Success : AuditResult.Failed,
            detail: problem);

        WeakReferenceMessenger.Default.Send(new LogMessage(
            problem is null ? $"引擎已重载（{_engine.Name}）" : $"引擎重载失败：{problem}",
            problem is null ? LogMsgLevel.Success : LogMsgLevel.Error));
    }

    /// <summary>
    /// 扫描流程里实际存在的模块与输出，生成可直接粘进配方的 json 片段。
    ///
    /// 解决的问题：`vm.outputs` 靠人工抄模块名，抄错一个字符要到第一帧才报"找不到模块"。
    /// 生成的是一份独立文件，而不是直接改配方 —— 自动改配置比抄错更危险。
    /// </summary>
    [RelayCommand]
    private void ScanOutputs()
    {
        if (_engine is not IOutputScanner scanner)
        {
            WeakReferenceMessenger.Default.Send(new LogMessage(
                $"当前引擎（{_engine.Name}）没有可扫描的模块输出，此功能只对 VM 流程有效",
                LogMsgLevel.Warning));
            return;
        }

        // 扫的是配方里已经声明的模块名 —— 目的是核对"这些名字对不对、有哪些输出"，
        // 不是凭空发现模块（SDK 的 GetAllModuleList 只给 ID，拿不到名字）。
        var moduleNames = _recipe.Vm.Outputs
            .Select(o => o.Module)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (moduleNames.Count == 0)
        {
            WeakReferenceMessenger.Default.Send(new LogMessage(
                "配方里还没有任何模块名（vm.outputs），先在配方里填一个模块名再扫描",
                LogMsgLevel.Warning));
            return;
        }

        var scan = scanner.Scan(moduleNames);
        if (scan.Error is not null)
        {
            WeakReferenceMessenger.Default.Send(new LogMessage(
                $"扫描失败：{scan.Error}", LogMsgLevel.Error));
            return;
        }

        try
        {
            var file = SaveScanResult(scan);
            var total = scan.Modules.Sum(m => m.Outputs.Count);
            var broken = scan.Modules.Count(m => m.Error is not null);

            WeakReferenceMessenger.Default.Send(new LogMessage(
                broken == 0
                    ? $"扫描完成：{scan.Modules.Count} 个模块 / {total} 个输出 → {file}"
                    : $"扫描完成：{scan.Modules.Count - broken}/{scan.Modules.Count} 个模块可用，"
                      + $"{broken} 个找不到（详见 {file}）",
                broken == 0 ? LogMsgLevel.Success : LogMsgLevel.Warning));
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new LogMessage(
                $"扫描结果写入失败：{ex.Message}", LogMsgLevel.Error));
        }
    }

    /// <summary>把扫描结果写成 json 片段，放到配方目录旁边，供人工复制。</summary>
    private string SaveScanResult(OutputScanResult scan)
    {
        var dir = Path.Combine(_paths.AssetRoot, "Config", "solutions");
        Directory.CreateDirectory(dir);

        var file = Path.Combine(dir, $"outputs-scan-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(file, BuildScanJson(scan, _engine.Name));
        return file;
    }

    /// <summary>
    /// 生成扫描结果的 json 文本。
    ///
    /// 抽成静态纯函数：文件写入不好测，但"生成的内容对不对"必须能测 ——
    /// ioKind 的推断错一项，配方就会拿错类型去取结果。
    /// </summary>
    internal static string BuildScanJson(OutputScanResult scan, string engineName)
    {
        var payload = new
        {
            scannedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            engine = engineName,
            note = "把 outputs 数组复制进配方（Config/recipes/*.json）的 vm.outputs；"
                 + "label 改成业务名（如「外径」），ioKind 按实际类型核对",
            outputs = scan.Modules
                .SelectMany(m => m.Error is not null
                    ? new[] { new { module = m.Name, label = "", role = "", io = "", ioKind = "",
                                     metric = "", unit = "", error = (string?)m.Error } }
                    : m.Outputs.Select(o => new
                {
                    module = m.Name,
                    label = $"{m.Name}.{o.Name}",
                    role = "",
                    io = o.Name,
                    ioKind = o.RuntimeType.Contains("Circle", StringComparison.OrdinalIgnoreCase)
                        ? "Circle"
                        : "Float",
                    metric = "",
                    unit = "",
                    error = (string?)null
                }))
                .ToList()
        };

        return JsonSerializer.Serialize(payload, ScanJsonOptions);
    }
}
