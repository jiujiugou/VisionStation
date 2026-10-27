using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VisionStation.Devices;
using VisionStation.Devices.Plc;

namespace VisionStation.App.Services.Inspection;

/// <summary>
/// 检测结果回写 PLC —— 每帧判定完成后把 OK/NG 写进结果寄存器。
///
/// <para>
/// <b>归属与线程</b>：
/// <list type="bullet">
///   <item>组合根创建的单例，活到进程结束；底层 PLC 连接（TCP 或 RTU）与触发源共用（谁 new 谁管）。</item>
///   <item>Enqueue 永不阻塞：结果先进有界队列，后台线程消费 —— PLC 断线重试（TCP 2s / 串口 500ms）
///         绝不能卡住 UI 线程和下一帧检测。</item>
///   <item>队列满 = PLC 侧跟不上或断了：丢最旧的并留日志。丢回写必须可见，
///         否则现场只看到"PLC 收不到判定"，查无对证。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>语义</b>（与 PLC 侧约定，AutoShop 变量监控表可直接验证）：
/// 结果寄存器 = 1 → OK；= 2 → NG。每帧判定后写一次，PLC 侧自行消费/复位。
/// </para>
/// </summary>
public sealed class PlcResultWriter : IDisposable
{
    /// <summary>OK 的编码。**不要改成 0**：0 会被 PLC 读成"还没写"，与"判定 OK"混为一谈。</summary>
    public const short OkCode = 1;

    /// <summary>NG 的编码。</summary>
    public const short NgCode = 2;

    /// <summary>
    /// 回写队列容量。16 是"PLC 短暂卡顿几十秒内不丢帧"与"断了不积压成内存垃圾"的折中：
    /// 正常节拍下队列深度几乎恒为 0~1，能堆到 16 说明 PLC 已经不在了。
    /// </summary>
    private const int QueueCapacity = 16;

    /// <summary>与触发源共用的 PLC 连接。**本类不负责关闭它**（谁 new 谁管，这里是组合根）。</summary>
    private readonly IPlcConnection _plc;

    /// <summary>结果寄存器地址（解析后的结构，不是字符串）</summary>
    private readonly PlcAddress _resultAddress;

    private readonly ILogger? _logger;

    /// <summary>
    /// 有界队列 + DropOldest：宁可丢最旧的一条回写，也不让生产端（检测线程）阻塞。
    /// 丢的是"最早的判定"，而最新的那一帧一定会写进去 —— 产线关心的是当前这一件。
    /// </summary>
    private readonly Channel<short> _queue = Channel.CreateBounded<short>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// 构造即启动后台消费线程。**不启动就没有任何回写会发生**，
    /// 而且不会有任何报错 —— 队列会安静地堆到 16 再开始丢。改这里要格外小心。
    /// </summary>
    /// <param name="plc">共享的 PLC 连接（由组合根创建并持有）</param>
    /// <param name="resultAddress">结果寄存器地址</param>
    /// <param name="logger">可为空；为空时回写失败与队列溢出都不会留痕（只在测试里这么用）</param>
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

    /// <summary>
    /// 后台消费循环：一条一条把队列里的判定写进 PLC。
    ///
    /// <para>
    /// <b>写失败不原地重试</b>：断线时的重连由 Hsl 自己负责，重试只会把队列越压越满，
    /// 最后把最新的判定挤掉。这里的策略是"记一笔，等下一帧"，靠后续帧自然恢复。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 停止消费并释放。**不等待队列写完**：关闭时用户的预期是"窗口马上消失"，
    /// 而不是"多等最多 16 条回写"。丢弃的条数已经由溢出日志负责留痕。
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
