using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using VisionStation.App.Composition.Lifecycle;
using VisionStation.App.Services.Inspection;
using VisionStation.Devices;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 检测页 ViewModel —— 这一页由哪些面板组成、编辑态是否打开、以及**触发来了怎么办**。
///
/// 检测本身不在这里：采图/检测/存图/存库在 <see cref="IInspectionWorkflow"/> 里跑，
/// 本类只做三件事：
///   1. 把触发信号排到 UI 线程上（<see cref="SynchronizationContext"/>）；
///   2. 防重入 —— 上一帧没跑完，这一帧直接丢；
///   3. 结果回来后广播 <see cref="InspectionResultMessage"/>，面板各自更新。
///
/// 线程约定（S4 的核心）：
///   - 工作流在后台线程跑，返回的位图已 Freeze；
///   - 广播发生在 **UI 线程**，所以订阅者里可以安全操作 ObservableCollection；
///   - 本类不出现 <c>Application.Current</c> / <c>Dispatcher</c>：同步上下文由组合根注入，
///     测试里传 null 就是"就地执行"，不需要 Application 实例。
/// </summary>
public partial class InspectionPageViewModel : ObservableObject, IDisposable
{
    /// <summary>存图连续失败到这个次数就升级为显式告警（不再是"偶发失败"）。</summary>
    private const int ImageSaveAlertThreshold = 10;

    private readonly IInspectionWorkflow _workflow;
    private readonly ITriggerSource _trigger;
    private readonly ICamera _camera;
    private readonly ShutdownState _shutdown;
    private readonly InFlightWorkMonitor _inFlight;
    private readonly SynchronizationContext? _ui;
    private readonly ILogger<InspectionPageViewModel> _logger;

    /// <summary>0 = 空闲，1 = 有一帧在跑。用 Interlocked 而不是 bool：触发来自线程池。</summary>
    private int _running;

    /// <summary>累计丢弃的触发数（上一帧没跑完，这一帧不排队）。</summary>
    private long _droppedTotal;

    // PLC 结果回写（可空：未启用 PLC 时为 null）。判定完成后把 OK/NG 写回结果寄存器。
    private readonly PlcResultWriter? _plcWriter;

    /// <summary>自上次报告以来丢弃的次数。</summary>
    private int _droppedPending;

    /// <summary>上次报告丢弃的时间（Environment.TickCount64，毫秒）。</summary>
    private long _droppedLastReport;

    /// <summary>丢弃报告的节流间隔：节拍压到 50ms 时每帧都丢，逐帧记日志等于把日志刷没。</summary>
    private const int DropReportIntervalMs = 5000;

    public ImagePanelViewModel ImagePanel { get; }
    public ResultPanelViewModel ResultPanel { get; }
    public LogPanelViewModel LogPanel { get; }
    public ControlPanelViewModel ControlPanel { get; }
    public RoiEditorViewModel RoiEditor { get; }

    /// <summary>ROI 编辑覆盖层是否打开。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImagePanelVisible))]
    private bool _isRoiEditorOpen;

    /// <summary>
    /// 原图面板与编辑覆盖层互斥：编辑时原图让位，退出编辑后回来。
    /// 用派生属性而不是在 XAML 里加反向转换器 —— 少一个转换器，也便于单测。
    /// </summary>
    public bool IsImagePanelVisible => !IsRoiEditorOpen;

    /// <param name="uiContext">
    /// UI 线程的同步上下文，由组合根在 UI 线程上捕获后注入。
    /// 为 null 表示"没有 UI 线程"（单元测试），此时触发就地执行。
    /// </param>
    public InspectionPageViewModel(
        ImagePanelViewModel imagePanel,
        ResultPanelViewModel resultPanel,
        LogPanelViewModel logPanel,
        ControlPanelViewModel controlPanel,
        RoiEditorViewModel roiEditor,
        IInspectionWorkflow workflow,
        ITriggerSource trigger,
        ICamera camera,
        ShutdownState shutdown,
        InFlightWorkMonitor inFlight,
        ILogger<InspectionPageViewModel> logger,
        PlcResultWriter? plcWriter = null,
        SynchronizationContext? uiContext = null)
    {
        ImagePanel = imagePanel;
        ResultPanel = resultPanel;
        LogPanel = logPanel;
        ControlPanel = controlPanel;
        RoiEditor = roiEditor;

        _workflow = workflow;
        _trigger = trigger;
        _camera = camera;
        _shutdown = shutdown;
        _inFlight = inFlight;
        _logger = logger;
        _plcWriter = plcWriter;
        _ui = uiContext;

        // 触发信号 → 本页。订阅放这里（而不是某个"主 VM"）是因为：
        // 谁拥有这一帧的界面，谁就该拥有这一帧的编排。
        _trigger.Triggered += OnTriggered;

        // 状态栏相机指示灯必须在启动时就反映真实状态，
        // 否则相机明明连上了，也要等第一帧检测跑完才亮。
        WeakReferenceMessenger.Default.Send(new CameraStatusMessage(_camera.IsAvailable));
        WeakReferenceMessenger.Default.Send(new LogMessage("VisionStation 已就绪", LogMsgLevel.Success));

        WeakReferenceMessenger.Default.Register<InspectionPageViewModel, RoisUpdatedMessage>(this,
            static (_, _) => WeakReferenceMessenger.Default.Send(
                new LogMessage("ROI 已更新，下一帧生效", LogMsgLevel.Info)));
    }

    // ═══════════════════════════════════════════════════════════
    //  触发 → 后台跑一帧 → 回到 UI 线程广播
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 触发回调。可能在任意线程上被调用（定时器在线程池，按钮在 UI 线程）。
    ///
    /// 关闭判断必须在这里、且在任何线程切换之前 —— 关闭流程可能停在任意线程上，
    /// 那时 Application 未必还可用。
    /// </summary>
    private void OnTriggered()
    {
        if (_shutdown.IsStopping)
        {
            _logger.LogDebug("关闭已开始，忽略本次触发");
            return;
        }

        if (_ui is null || ReferenceEquals(SynchronizationContext.Current, _ui))
        {
            _ = RunFrameAsync();
            return;
        }

        // 只切这一次线程：后续 await 会自动回到这个上下文，
        // 所以消息广播、面板更新天然在 UI 线程上，面板不需要各自再切一次。
        _ui.Post(static state => _ = ((InspectionPageViewModel)state!).RunFrameAsync(), this);
    }

    private async Task RunFrameAsync()
    {
        // 防重入：上一帧没跑完，这一帧丢弃（不排队 —— 排队的帧只会让界面追不上现场）
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            ReportDroppedFrame();
            return;
        }

        // 在途登记：关闭流程靠这个计数回答"还有没有一帧在跑"。
        // 租约覆盖工作流执行 + 结果广播，最后一条消息发完才算收尾。
        using var lease = _inFlight.Begin();

        try
        {
            var result = await _workflow.RunOnceAsync(_shutdown.Token).ConfigureAwait(true);
            if (result is null) return;

            // 判定完成即回写 PLC（内部有界队列，永不阻塞 UI）。
            // 放在 Publish 前：产线上 PLC 拿到判定才能动剔除机构，越早越好。
            _plcWriter?.Write(result.Report.IsOk);

            // 到这里已经回到 UI 线程（await 捕获了构造时注入的上下文）
            Publish(result);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("本帧已取消（应用正在关闭）");
        }
        catch (Exception ex)
        {
            // 事件处理器是 async void 语义的起点，异常绝不能逃出去 —— 那会直接终止进程
            _logger.LogError(ex, "检测异常");
            WeakReferenceMessenger.Default.Send(new LogMessage($"异常: {ex.Message}", LogMsgLevel.Error));
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>
    /// 记录一次"因上一帧未完成而丢弃的触发"。
    ///
    /// 丢弃本身是设计（不排队，否则界面永远追不上现场），但**丢弃必须可见**：
    /// 节拍 50ms、单帧 200ms 时每 4 次触发就丢 3 次，界面上完全看不出来，
    /// 现场只会觉得"偶尔漏检"，事后翻日志一条证据都没有。
    ///
    /// 节流 5 秒一条：逐帧记会把日志刷没，反而把真正的问题埋掉。
    /// </summary>
    private void ReportDroppedFrame()
    {
        var total = Interlocked.Increment(ref _droppedTotal);
        Interlocked.Increment(ref _droppedPending);

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _droppedLastReport);
        if (now - last < DropReportIntervalMs) return;

        // 只有一个线程能完成这次跃迁，避免同一时刻冒出多条汇总
        if (Interlocked.CompareExchange(ref _droppedLastReport, now, last) != last) return;

        var reported = Interlocked.Exchange(ref _droppedPending, 0);
        if (reported <= 0) return;

        _logger.LogWarning("上一帧未完成，已丢弃 {Reported} 次触发（累计 {Total} 次）：单帧耗时超过触发间隔",
            reported, total);
        WeakReferenceMessenger.Default.Send(new LogMessage(
            $"节拍跟不上：已丢弃 {reported} 次触发（累计 {total} 次）", LogMsgLevel.Warning));
    }

    /// <summary>把一帧的结果广播给面板。必须在 UI 线程调用。</summary>
    private static void Publish(InspectionRunResult result)
    {
        WeakReferenceMessenger.Default.Send(new InspectionResultMessage(result));
        WeakReferenceMessenger.Default.Send(new CameraStatusMessage(result.CameraOnline));

        if (result.ImageError is { } error)
        {
            WeakReferenceMessenger.Default.Send(new LogMessage(
                $"存图失败（连续 {result.ConsecutiveImageFailures} 次）: {error}", LogMsgLevel.Warning));

            if (result.ConsecutiveImageFailures == ImageSaveAlertThreshold)
            {
                WeakReferenceMessenger.Default.Send(new LogMessage(
                    $"存图已连续失败 {ImageSaveAlertThreshold} 次，图片追溯中断，请检查磁盘空间与写权限",
                    LogMsgLevel.Error));
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  ROI 编辑态
    // ═══════════════════════════════════════════════════════════

    /// <summary>F2：进入/退出 ROI 编辑。</summary>
    [RelayCommand]
    private void ToggleRoiEditor()
    {
        if (!IsRoiEditorOpen)
        {
            // 进入编辑时把当前图交给编辑器：编辑器要看着底图画框。
            // 还没采过图时 OriginalImage 为 null，编辑器自己显示空态。
            RoiEditor.Image = ImagePanel.OriginalImage;
        }

        IsRoiEditorOpen = !IsRoiEditorOpen;
    }

    /// <summary>
    /// 离开本页时调用。编辑态不退出的话，覆盖层会跟着用户漂到别的页面上去。
    /// </summary>
    public void ExitEditMode() => IsRoiEditorOpen = false;

    public void Dispose()
    {
        _trigger.Triggered -= OnTriggered;
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}
