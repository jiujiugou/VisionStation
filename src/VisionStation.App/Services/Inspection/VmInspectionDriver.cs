using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using VisionStation.Core.Models;
using VisionStation.Devices.Vm;
using VisionStation.Engines.Vm;

namespace VisionStation.App.Services.Inspection;

/// <summary>
/// VM 模式（TCP 中转）的驱动：收 VisionMaster 推来的结果 → 入库 → 广播给界面。
///
/// 它和 <see cref="InspectionPageViewModel"/> 的自动循环是**两种互斥的驱动方式**：
///   · 自动循环：程序按定时器/PLC 触发去采图，然后调引擎；
///   · 本类：VM 采图、VM 判定，推过来什么就是什么。
/// 两者的下半段（存库 / 回 PLC / 刷新界面）走的是同一条路，所以结果消息也发同一个。
///
/// 线程归属：接收回调在 IO 线程上，**广播必须回到 UI 线程**（面板更新绑定属性）。
/// </summary>
public sealed class VmInspectionDriver : IDisposable
{
    /// <summary>丢帧告警阈值：累计到这个数就在界面日志里说一句</summary>
    private const int DropAlertThreshold = 5;

    private readonly IInspectionWorkflow _workflow;
    private readonly VmResultListener _listener;
    private readonly IReadOnlyList<VmOutputMapping> _outputs;
    private readonly ILogger<VmInspectionDriver> _logger;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>多久没收到结果算断流。业务参数，来自 appsettings，不在代码里写死。</summary>
    private readonly TimeSpan _stallTimeout;

    /// <summary>链路检查定时器。每秒比对一次，**只在状态翻面时说话**。</summary>
    private System.Threading.Timer? _linkTimer;

    /// <summary>工位是否在运行中（由 TriggerStateMessage 驱动）。停机时没结果不该报警。</summary>
    private volatile bool _isRunning;

    /// <summary>上一次广播出去的状态。只在定时器线程读写。</summary>
    private VmLinkState _lastState = VmLinkState.NotListening;

    /// <summary>1 = 正在处理一帧。VM 推得快时靠它串行化，避免并发写库。</summary>
    private int _busy;

    private int _droppedTotal;
    private int _receivedTotal;

    private bool _disposed;

    /// <param name="outputs">
    /// 配方的输出映射。分隔符格式的结果没有字段名，只能按位置对应这张表；
    /// JSON 格式自带 label，用不上它。
    /// </param>
    public VmInspectionDriver(
        string bindAddress,
        int port,
        IInspectionWorkflow workflow,
        IReadOnlyList<VmOutputMapping> outputs,
        ILogger<VmInspectionDriver> logger,
        TimeSpan stallTimeout,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(logger);
        // 第三个参数不传：ThrowIfLessThanOrEqual 的 paramName 带 [CallerArgumentExpression]，
        // 手写 nameof 会被 S3236 判为"遮蔽调用方信息"，而且本来就是多余的。
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stallTimeout, TimeSpan.Zero);

        _workflow = workflow;
        _outputs = outputs;
        _logger = logger;
        _stallTimeout = stallTimeout;

        _listener = new VmResultListener(bindAddress, port, OnLine,
            loggerFactory?.CreateLogger("VisionStation.Devices.Vm.VmResultListener"));
    }

    /// <summary>监听地址（界面与自检显示用）</summary>
    public string Endpoint => _listener.Endpoint;

    /// <summary>累计收到的帧数</summary>
    public int ReceivedTotal => Volatile.Read(ref _receivedTotal);

    /// <summary>累计丢弃的帧数（上一帧还没处理完）</summary>
    public int DroppedTotal => Volatile.Read(ref _droppedTotal);

    /// <summary>
    /// 开始监听并启动链路检查。端口被占用会抛出 —— 启动期就该红，不能"起了但没在听"。
    /// </summary>
    public void Start()
    {
        _listener.Start();

        // 运行状态从控制面板的广播里拿。用带接收者的 Register 重载，是弱引用，
        // 不担心这个单例被消息中心钉住；Stop 里对应 Unregister。
        WeakReferenceMessenger.Default.Register<VmInspectionDriver, TriggerStateMessage>(this,
            static (driver, msg) => driver._isRunning = msg.Value);

        // 1 秒一次：比断流阈值小一个量级，阈值 10 秒时最多晚 1 秒发现。
        _linkTimer ??= new System.Threading.Timer(CheckLink, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>停止监听。</summary>
    public void Stop()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* 已释放 */ }

        _listener.Stop();

        _linkTimer?.Dispose();
        _linkTimer = null;

        WeakReferenceMessenger.Default.Unregister<TriggerStateMessage>(this);

        _lastState = VmLinkState.NotListening;
    }

    /// <summary>
    /// 链路检查（定时器线程）。**只在状态翻面时说话** ——
    /// 每秒发一条消息会让界面日志被刷屏，操作员会把真告警淹掉。
    /// </summary>
    private void CheckLink(object? state)
    {
        try
        {
            var nowUtc = DateTime.UtcNow;
            var lastLineUtc = _listener.LastLineUtc;

            // 基准取"最后一条结果"，没有则取"本轮连接建立" ——
            // 后者保证 VM 重连后的头几秒不会被误判成断流。
            var lastActivityUtc = lastLineUtc ?? _listener.ConnectedAtUtc;

            var stateNow = VmLinkMonitor.Evaluate(
                _listener.IsListening,
                _listener.IsClientConnected,
                lastActivityUtc,
                nowUtc,
                _stallTimeout);

            if (stateNow == _lastState) return;

            var previous = _lastState;
            _lastState = stateNow;

            var status = new VmLinkStatus(
                stateNow,
                _listener.Endpoint,
                lastLineUtc?.ToLocalTime(),
                lastLineUtc is { } t ? nowUtc - t : null,
                _stallTimeout,
                _isRunning);

            ReportTransition(previous, status);

            SendToUi(() => WeakReferenceMessenger.Default.Send(new VmLinkStatusMessage(status)));
        }
        catch (Exception ex)
        {
            // 定时器回调里抛异常会终止后续调度，链路监控就此哑掉且没人知道
            _logger.LogError(ex, "[VM链路] 链路检查异常（下一拍会继续）");
        }
    }

    /// <summary>
    /// 状态翻面时的说法。
    ///
    /// 分级别是刻意的：**运行中断流是 Error，停机后没结果是 Info**。
    /// 不这么分，产线一停就刷告警，操作员很快学会无视它 —— 那时真出事也没人看。
    /// </summary>
    private void ReportTransition(VmLinkState previous, VmLinkStatus status)
    {
        var threshold = $"{_stallTimeout.TotalSeconds:0.#} 秒";

        switch (status.State)
        {
            case VmLinkState.NoClient:
                if (previous is VmLinkState.Healthy or VmLinkState.Stalled)
                    Warn($"VisionMaster 连接已断开（监听 {status.Endpoint}），等待重连");
                else
                    Info($"已监听 {status.Endpoint}，等待 VisionMaster 连接");
                break;

            case VmLinkState.Healthy:
                if (previous is VmLinkState.Stalled)
                    Info($"VM 链路恢复：{FormatSince(status.SinceLast)}前收到结果");
                else if (previous is VmLinkState.NoClient)
                    Info($"VisionMaster 已连接（{status.Endpoint}）");
                break;

            case VmLinkState.Stalled:
                var since = FormatSince(status.SinceLast);
                if (status.IsRunning)
                {
                    Error($"已 {since}没有收到 VM 结果（阈值 {threshold}）：检查 VisionMaster 是否在连续运行、"
                        + $"发送数据模块是否被执行、触发信号是否到位");
                }
                else
                {
                    // 停机时没结果本来就是正常的，只记为信息，不改界面告警色
                    Info($"已 {since}没有收到 VM 结果（工位未运行，可能是正常停机；阈值 {threshold}）");
                }
                break;
        }
    }

    private static string FormatSince(TimeSpan? since)
    {
        if (since is null) return "从未收到结果，已经 ";
        var s = since.Value;
        return s.TotalSeconds < 60
            ? $"{s.TotalSeconds:0.#} 秒"
            : $"{(int)s.TotalMinutes} 分 {s.Seconds} 秒";
    }

    private void Info(string text)
    {
        _logger.LogInformation("[VM链路] {Text}", text);
        SendLog(text, LogMsgLevel.Info);
    }

    private void Warn(string text)
    {
        _logger.LogWarning("[VM链路] {Text}", text);
        SendLog(text, LogMsgLevel.Warning);
    }

    private void Error(string text)
    {
        _logger.LogError("[VM链路] {Text}", text);
        SendLog(text, LogMsgLevel.Error);
    }

    private static void SendLog(string text, LogMsgLevel level) =>
        SendToUi(() => WeakReferenceMessenger.Default.Send(new LogMessage(text, level)));

    /// <summary>一条结果到达（IO 线程）。</summary>
    private void OnLine(string line)
    {
        if (Volatile.Read(ref _busy) == 1)
        {
            // 上一帧还在写库。宁可丢弃并计数，也不要并发写同一个 SQLite 连接 ——
            // 但**必须计数并说出来**：产线上漏掉一件比判一件不良严重得多。
            var total = Interlocked.Increment(ref _droppedTotal);
            _logger.LogWarning("[VM链路] 上一帧尚未处理完，丢弃本帧（累计丢弃 {Total} 次）", total);

            if (total % DropAlertThreshold == 0)
            {
                SendToUi(() => WeakReferenceMessenger.Default.Send(new LogMessage(
                    $"VM 结果处理跟不上：累计丢弃 {total} 帧（上一帧未完成），请检查磁盘与数据库",
                    LogMsgLevel.Warning)));
            }

            return;
        }

        Interlocked.Exchange(ref _busy, 1);
        try
        {
            Interlocked.Increment(ref _receivedTotal);

            // 解析失败也返回一份 NG 报告（带原因），所以这里不会因为格式问题静默丢帧
            var report = VmResultParser.Parse(line, triggerSourceFallback: "VM", outputs: _outputs);

            var result = _workflow.AcceptExternalAsync(report, _cts.Token).GetAwaiter().GetResult();
            if (result is null) return;

            SendToUi(() => Publish(result));
        }
        catch (OperationCanceledException)
        {
            // 正在停机
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VM链路] 处理结果失败");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>把一帧的结果广播给面板（UI 线程）。与自动模式走同一条消息。</summary>
    private static void Publish(InspectionRunResult result)
    {
        WeakReferenceMessenger.Default.Send(new InspectionResultMessage(result));
        WeakReferenceMessenger.Default.Send(new CameraStatusMessage(result.CameraOnline));
    }

    /// <summary>回 UI 线程执行；没有 WPF 环境（单测）时直接执行。</summary>
    private static void SendToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        try { _cts.Dispose(); }
        catch (ObjectDisposedException) { /* 重复释放 */ }

        _listener.Dispose();
    }
}
