using VisionStation.Devices.Plc;

namespace VisionStation.Devices;

/// <summary>
/// PLC 触发源 — 轮询"拍照请求"寄存器，上升沿触发检测
///
/// 握手约定（汇川 AutoShop 仿真/真机一致）：
///   1. PLC 把触发寄存器写 1；
///   2. 本源在下一轮询读到 上升沿（0→非0）→ 触发检测；
///   3. 触发后本源**回写 0**（自清除）——PLC 只需置位一次，不用等检测完成再复位。
///
/// 线程模型照抄 TimerTrigger（同款 CAS 单消费者），轮询跑在 ThreadPool：
///   - 触发回调抛异常不能逃逸（那会终止进程），就地拦下交给 onError；
///   - 同一时刻只有一个消费者在跑，消费者侧的防重入（_isProcessing）照旧需要。
///
/// 断线自愈：
///   每轮询先看连接状态，断了就先 Connect 再读——Hsl 底层在长连接模式下
///   失败后下一笔请求本会自动重连，这里显式重试只是把"恢复时间"压到
///   一个轮询周期内，并且让 LastError 始终有最新原因。
///
/// 归属：ModbusTcpPlc 连接由组合根创建（DI 单例，与本源、结果回写共用），
/// 本源只管轮询，Dispose 时**不关连接**——谁 new 的连接谁 dispose。
/// </summary>
public sealed class PlcTriggerSource : ITriggerSource
{
    private readonly ModbusTcpPlc _plc;
    private readonly PlcAddress _triggerAddress;
    private readonly PlcAddress? _heartbeatAddress;
    private readonly System.Timers.Timer _timer;
    private readonly Action<Exception>? _onError;
    private int _running;
    private int _busy;
    private volatile bool _disposed;
    private short _previousValue;          // 上升沿检测：上一轮询读到的触发寄存器值
    private short _heartbeatCounter;       // 心跳：每次轮询 +1，PLC 侧看到它变化就知道软件活着

    public event Action? Triggered;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>底层连接状态（状态栏/自检展示用）</summary>
    public bool IsConnected => _plc.IsConnected;

    /// <summary>底层连接（组合根据此把同一连接交给结果回写器——一个连接，两处共用）</summary>
    public ModbusTcpPlc ConnectedPlc => _plc;

    /// <summary>最近一次失败的人话原因（与 IDevice.LastError 同一约定）</summary>
    public string? LastError => _plc.LastError;

    public PlcTriggerSource(ModbusTcpPlc plc, PlcAddress triggerAddress,
        PlcAddress? heartbeatAddress, int pollIntervalMs,
        Action<Exception>? onError = null)
    {
        _plc = plc;
        _triggerAddress = triggerAddress;
        _heartbeatAddress = heartbeatAddress;
        _onError = onError;
        _timer = new System.Timers.Timer(pollIntervalMs);
        _timer.Elapsed += (_, _) => PollOnce();
        _timer.AutoReset = true;
    }

    private void PollOnce()
    {
        if (_disposed) return;

        try
        {
            // 心跳先行：哪怕断线导致读取失败，重连尝试本身也是心跳语义的一部分
            if (_heartbeatAddress is not null)
                _plc.WriteShort(_heartbeatAddress.Value, ++_heartbeatCounter);

            if (!_plc.IsConnected && !_plc.Connect())
                return;   // 连不上：LastError 已由 Connect 填好，下一周期再试

            var current = _plc.ReadShort(_triggerAddress);
            if (current is null) return;   // 读失败：LastError 已填，保持上次的沿状态不乱触发

            var risingEdge = _previousValue == 0 && current.Value != 0;
            _previousValue = current.Value;

            if (risingEdge)
            {
                // 自清除：失败不阻塞触发（下一轮询 current 非 0 且 previous 非 0，不会重复触发；
                // 但 PLC 侧会看到 1 没被清掉——这种情况下 LastError 已留痕）
                _plc.WriteShort(_triggerAddress, 0);
                _previousValue = 0;

                // CAS 单消费者：上一帧还在处理就丢弃本次触发（与 TimerTrigger 同语义）
                if (Interlocked.CompareExchange(ref _busy, 1, 0) == 1) return;
                try { Triggered?.Invoke(); }
                catch (Exception ex) { _onError?.Invoke(ex); }
                finally { Interlocked.Exchange(ref _busy, 0); }
            }
        }
        catch (Exception ex)
        {
            // Hsl 一般用 OperateResult 报错不抛异常，这里是兜底：轮询线程死不得
            _onError?.Invoke(ex);
        }
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) == 0)
        {
            _timer.Start();
            // 立即试连一次：不等第一个轮询周期（默认 100ms，断线重试 2s 超时）
            // —— 否则启动后状态栏要闪一阵假的"PLC离线"才变成真实状态
            Task.Run(PollOnce);
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) == 1) _timer.Stop();
    }

    /// <summary>手动触发一次（PLC 模式下界面按钮仍可用）</summary>
    public void Fire() => Triggered?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        GC.SuppressFinalize(this);
        // 不 Dispose _plc：连接是组合根的单例，结果回写还要用
    }
}
