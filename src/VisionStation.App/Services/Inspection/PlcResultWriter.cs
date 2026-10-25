using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VisionStation.Devices;
using VisionStation.Devices.Plc;

namespace VisionStation.App.Services.Inspection;

/// <summary>
/// 检测结果回写 PLC — 每帧判定完成後把 OK/NG 写进结果寄存器
///
/// 归属与线程：
///   - 组合根创建的单例，活到进程结束；底层 PLC 连接（TCP 或 RTU）与触发源共用（谁 new 谁管）。
///   - Enqueue 永不阻塞：结果先进有界队列，后台线程消费——PLC 断线重试（TCP 2s / 串口 500ms）
///     绝不能卡住 UI 线程和下一帧检测。
///   - 队列满 = PLC 侧跟不上或断了：丢最旧的并留日志。丢回写必须可见，
///     否则现场只看到"PLC 收不到判定"，查无对证。
///
/// 语义（与 PLC 侧约定，AutoShop 变量监控表可直接验证）：
///   结果寄存器 = 1 → OK；= 2 → NG。每帧判定后写一次，PLC 侧自行消费/复位。
/// </summary>
public sealed class PlcResultWriter : IDisposable
{
    public const short OkCode = 1;
    public const short NgCode = 2;

    private const int QueueCapacity = 16;

    private readonly IPlcConnection _plc;
    private readonly PlcAddress _resultAddress;
    private readonly ILogger? _logger;
    private readonly Channel<short> _queue = Channel.CreateBounded<short>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();

    public PlcResultWriter(IPlcConnection plc, PlcAddress resultAddress, ILogger? logger = null)
    {
        _plc = plc;
        _resultAddress = resultAddress;
        _logger = logger;
        _ = Task.Run(() => ConsumeAsync(_cts.Token), _cts.Token);
    }

    /// <summary>回写一帧判定。非阻塞——判定为 true 写 1（OK），false 写 2（NG）。</summary>
    public void Write(bool isOk)
    {
        if (!_queue.Writer.TryWrite(isOk ? OkCode : NgCode))
            _logger?.LogWarning("PLC 结果回写队列已满（容量 {Cap}），丢弃最旧的一帧回写", QueueCapacity);
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var code in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // 写失败只记日志：断线时重试循环由"下一帧照常入队 + Hsl 自动重连"兜底，
                // 这里原地重试反而会把队列越压越满。
                if (!_plc.WriteShort(_resultAddress, code))
                    _logger?.LogWarning("PLC 结果回写失败：{Error}", _plc.LastError);
            }
        }
        catch (OperationCanceledException) { /* 关闭即退出 */ }
        catch (Exception ex)
        {
            // 后台消费线程死不得：死了之后所有回写都悄悄堆在队列里没人管
            _logger?.LogError(ex, "PLC 结果回写线程异常退出前的最后一笔");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
