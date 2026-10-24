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
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(logger);

        _workflow = workflow;
        _outputs = outputs;
        _logger = logger;

        _listener = new VmResultListener(bindAddress, port, OnLine,
            loggerFactory?.CreateLogger("VisionStation.Devices.Vm.VmResultListener"));
    }

    /// <summary>监听地址（界面与自检显示用）</summary>
    public string Endpoint => _listener.Endpoint;

    /// <summary>累计收到的帧数</summary>
    public int ReceivedTotal => Volatile.Read(ref _receivedTotal);

    /// <summary>累计丢弃的帧数（上一帧还没处理完）</summary>
    public int DroppedTotal => Volatile.Read(ref _droppedTotal);

    /// <summary>开始监听。端口被占用会抛出 —— 启动期就该红，不能"起了但没在听"。</summary>
    public void Start() => _listener.Start();

    /// <summary>停止监听。</summary>
    public void Stop()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* 已释放 */ }

        _listener.Stop();
    }

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
