using VisionStation.Devices.Plc;

namespace VisionStation.Devices;

/// <summary>
/// PLC 触发源 — 轮询"拍照请求"寄存器，上升沿触发检测
///
/// 握手约定（汇川 AutoShop 仿真/真机一致）：
///   1. PLC 把触发寄存器写 1；
///   2. 本源在下一轮询读到 上升沿（0→非0）→ 触发检测；
///   3. 触发后本源**回写 0**（自清除）——PLC 只需置位一次，不用等检测完成再复位。
///      回写失败时**不会**把同一次触发再处理一遍，而是在后续轮询里重试清位（见 <c>_pendingClear</c>）。
///
/// <para>
/// <b>定位</b>：产线握手的"上半段"。它在整条链路里的位置是——
/// <c>D100 上升沿 → 清 D100（应答）→ 心跳照写 D102 → Triggered(Plc) →
/// InspectionPageViewModel.OnTriggered → InspectionWorkflow.RunOnceAsync</c>。
/// 它**只管握手**，不采图、不判定、不写结果寄存器（D101 由 VM 推送帧驱动）。
/// </para>
///
/// <para>
/// ⚠ <b>已知设计缺口："半假触发"（当前最该说清的一条）</b>
/// <br/>
/// <c>InspectionWorkflow.RunOnceAsync</c> 现在是空实现：第一行记 Debug 就返回 null，一个字都不检测。
/// 所以上面这条链路**到 RunOnceAsync 就断了**，D100 的实际作用只有"握手 + 让 PLC 侧累计 D106 触发计数"，
/// 不驱动任何检测；D101 只随 VM 推来的帧变化。
/// 这是架构收敛到"判定权归 VM"之后留下的接缝，不是 bug：
/// 产线上谁管相机，谁就负责发起触发 —— VM 模式下本程序**不该**发起触发，
/// 只需要"得知有这一件、并把计数对上"。缺的是把这句话落到代码/文档里。
/// <br/>
/// 两种修法（**都未拍板，不要在这里擅自选一种**）：
/// <list type="number">
///   <item><b>按引擎分流触发源</b>：VM 模式下不轮询 D100，改由 VM 自己收触发。
///         要动 <c>ITriggerSource</c> 的选择逻辑与配方语义，改动面在组合根与配方层。</item>
///   <item><b>文档承认现状</b>：写清"本程序不参与触发"，D100 只剩心跳与计数价值，
///         并去掉界面/文档里"触发即检测"的措辞。</item>
/// </list>
/// 在拍板之前，任何"让 D100 真的跑检测"的改动都会把判定权拽回本程序，
/// 与"判定权归 VM"的定位直接冲突 —— 动手前先确认定位。
/// </para>
///
/// <para>
/// <b>归属</b>：<c>Plc:Enabled=true</c> 时由组合根的 <c>AddDevices</c> 创建，DI 单例、活到进程结束。
/// 与 <c>PlcResultWriter</c> **共用同一个 <see cref="IPlcConnection"/>**（连接是单例，
/// 通过 <see cref="ConnectedPlc"/> 交出去）。因此轮询线程与回写线程会同时打同一根串口/socket，
/// 并发安全由连接实现自己的对象锁负责，本类不再加锁。
/// <see cref="Dispose"/> **不关连接** —— 谁 new 的连接谁 dispose（组合根）。
/// </para>
///
/// <para>
/// <b>线程</b>：轮询跑在 <see cref="System.Timers.Timer"/> 的线程池回调上。
/// <see cref="Start"/> 时还会 <c>Task.Run(PollOnce)</c> 立刻试连一次，所以启动瞬间
/// 可能与第一个 Tick 并行 —— 本类**没有整体重入锁**（只有 <c>_busy</c> 护住回调那一段），
/// <c>_previousValue</c> / <c>_heartbeatCounter</c> / <c>_pendingClear</c> 都是非原子字段。
/// 正常情况单次轮询 &lt; 一个周期，不会重叠；但断线时 Connect 可能耗到 2s 超时，
/// 期间定时器会再排回调，两个 PollOnce 就会并行。后果是心跳可能跳号、
/// 沿状态可能被覆盖（两种方向都可能：重复触发，或漏掉一次触发），
/// 不致命但要知道它存在。要彻底解决得在整个 PollOnce 外面加 Interlocked 守卫。
/// </para>
///
/// <para>
/// <b>失败行为</b>：全部内部吞掉，绝不外抛（轮询跑在线程池，逃出去会终止进程）。
/// 连接/读写失败的原因由连接对象写进 <see cref="LastError"/>，
/// 其它异常交给构造时注入的 <c>onError</c>（组合根接到 ILogger）。失败后不退出循环，下一周期再试。
/// </para>
///
/// <para>
/// <b>断线自愈</b>：每轮询先看连接状态，断了就先 Connect 再读——Hsl 底层在长连接模式下
/// 失败后下一笔请求本会自动重连，这里显式重试只是把"恢复时间"压到一个轮询周期内，
/// 并且让 LastError 始终有最新原因。
/// </para>
/// </summary>
public sealed class PlcTriggerSource : ITriggerSource
{
    private readonly IPlcConnection _plc;
    private readonly PlcAddress _triggerAddress;
    private readonly PlcAddress? _heartbeatAddress;
    private readonly System.Timers.Timer _timer;
    private readonly Action<Exception>? _onError;
    private int _running;
    private int _busy;
    private volatile bool _disposed;
    private short _previousValue;          // 上升沿检测：上一轮询读到的触发寄存器值
    private short _heartbeatCounter;       // 心跳：每次轮询 +1，PLC 侧看到它变化就知道软件活着

    /// <summary>
    /// 上一轮的"清位"没有确认成功（F-17）。为 true 时下一轮**只重试清位，不判沿**。
    ///
    /// <para>
    /// 为什么需要这个字段而不是单靠 <see cref="_previousValue"/>：清位失败后触发寄存器还是非 0，
    /// 如果把 <see cref="_previousValue"/> 保留成非 0 就没人再去清位了 —— PLC 侧 D100 会一直挂着 1，
    /// 之后永远读不到新的上升沿（卡死）。反过来置 0 又会被当成"又来了一件"（重复触发）。
    /// 所以记忆分成两件事：沿状态（<see cref="_previousValue"/>）和"欠 PLC 一次清位"（本字段）。
    /// </para>
    ///
    /// <para>
    /// 与非原子字段同一个前提，见类注释的「线程」一节：只在轮询回调里读写，
    /// 断线重连导致两个 PollOnce 并行时可能与 <see cref="_previousValue"/> 一起被覆盖（不致命）。
    /// </para>
    /// </summary>
    private bool _pendingClear;

    public event Action<TriggerOrigin>? Triggered;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>底层连接状态（状态栏/自检展示用）。语义是"最近一笔通信是否成功"，不是物理链路状态。</summary>
    public bool IsConnected => _plc.IsConnected;

    /// <summary>底层连接（组合根据此把同一连接交给结果回写器——一个连接，两处共用）</summary>
    public IPlcConnection ConnectedPlc => _plc;

    /// <summary>最近一次失败的人话原因（与 IDevice.LastError 同一约定）</summary>
    public string? LastError => _plc.LastError;

    /// <param name="plc">共用连接（组合根持有并释放）；本类只读写，不 Dispose</param>
    /// <param name="triggerAddress">触发寄存器，默认 D100。必须是保持寄存器，否则每次读取都会被驱动拒绝</param>
    /// <param name="heartbeatAddress">心跳寄存器，默认 D102；null = 不写心跳（测试用）</param>
    /// <param name="pollIntervalMs">轮询周期，默认 100ms。启动期已校验 &gt; 0</param>
    /// <param name="onError">非通信类异常的出口（异常本身已在连接层被转成 LastError）</param>
    public PlcTriggerSource(IPlcConnection plc, PlcAddress triggerAddress,
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

    /// <summary>
    /// 一次轮询：写心跳 → 必要时重连 → 读触发寄存器 →（欠清位则只补清位）→ 判上升沿 → 自清除 → 发事件。
    ///
    /// <para>
    /// <b>顺序不能随意调</b>：心跳排在最前面，是因为它是"软件活着"的唯一证据；
    /// 若放在读触发之后，PLC 侧一旦读超时，心跳也跟着断，PLC 会同时收到
    /// "没心跳"和"没触发"两个信号，反而分不清是软件死了还是没料。
    /// </para>
    /// </summary>
    private void PollOnce()
    {
        if (_disposed) return;

        try
        {
            // 心跳先行：哪怕断线导致读取失败，重连尝试本身也是心跳语义的一部分
            //
            // 注意：写失败 counter 也照样 +1（自增在前），所以重连后会看到数字跳一段 ——
            // 这是刻意的，PLC 侧用 `<>` 比较只关心"变了没有"，不关心连续。
            // short 会从 32767 回绕到 -32768，同样因为用 `<>` 而不受影响；**别改成 `>` 比较**。
            if (_heartbeatAddress is not null)
                _plc.WriteShort(_heartbeatAddress.Value, ++_heartbeatCounter);

            if (!_plc.IsConnected && !_plc.Connect())
                return;   // 连不上：LastError 已由 Connect 填好，下一周期再试

            var current = _plc.ReadShort(_triggerAddress);
            if (current is null) return;   // 读失败：LastError 已填，保持上次的沿状态不乱触发

            // ── 欠一次清位：本轮只重试清位，绝不判沿（F-17）────────────────────────
            //
            // 上一轮判定为上升沿但清位写失败时，PLC 侧 D100 仍是非 0。这里若继续走下面的
            // 沿判定，`_previousValue == 0` 会把它认成"新的一次触发"——同一次触发被处理两次，
            // 库里多一条记录、PLC 收两次结果。所以重试轮直接 return，不碰 Triggered。
            if (_pendingClear)
            {
                if (current.Value == 0)
                {
                    // PLC 自己把位回了 0（它有超时复位，或别的通道清过）：欠的清位自然了结。
                    _pendingClear = false;
                    _previousValue = 0;
                    return;
                }

                if (_plc.WriteShort(_triggerAddress, 0))
                {
                    _pendingClear = false;
                    _previousValue = 0;
                    // 本轮不发事件：这一次触发在上一轮已经发过了，重试只负责把位清掉。
                }
                else
                {
                    // 还是清不掉：保持 _previousValue 非 0，下一轮继续重试。
                    // **不能退回 0** —— 那正是重复触发的成因。
                    _previousValue = current.Value;
                }

                return;
            }

            var risingEdge = _previousValue == 0 && current.Value != 0;
            _previousValue = current.Value;

            if (risingEdge)
            {
                // 自清除（接收应答）：先清位再回调，PLC 不必等检测完成。
                //
                // ⚠ 清位成败决定 _previousValue 怎么收尾，这是 F-17 的全部：
                //   · 成功 → 置 0：D100 确实回到 0，下一轮读到 1 才算新沿；
                //   · 失败 → 保留非 0 + 打上 _pendingClear：下一轮进上面的重试分支，不判沿。
                // 旧代码无条件置 0，于是"清位失败 + PLC 侧还是 1"被当成新沿，同一次触发跑两遍。
                //
                // 事件仍然照发一次：料确实到位、PLC 也确实置了位，"这一件丢掉"会让 PLC
                // 等一个永远不会来的结果，比多跑一帧更难查。重试分支保证它只发这一次。
                var cleared = _plc.WriteShort(_triggerAddress, 0);
                if (cleared)
                {
                    _previousValue = 0;
                }
                else
                {
                    _previousValue = current.Value;   // 非 0：压制下一轮的误判
                    _pendingClear = true;             // 记住"还欠 PLC 一次清位"
                }

                // CAS 单消费者：上一帧还在处理就丢弃本次触发（与 TimerTrigger 同语义）
                //
                // 注意丢弃发生在**已经清位之后**：PLC 认为这一件已经被接收，
                // 而本程序这边连事件都没发。D106（PLC 侧触发计数）会因此比本程序的处理数多 ——
                // 这正是"丢帧对账尚未实现"那条待办要暴露的东西。
                if (Interlocked.CompareExchange(ref _busy, 1, 0) == 1) return;
                try { Triggered?.Invoke(TriggerOrigin.Plc); }
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

    /// <summary>
    /// 启动轮询。幂等（<see cref="_running"/> 用 Interlocked 翻转）。
    ///
    /// <para>
    /// 启动后立刻在线程池上跑一次 <see cref="PollOnce"/>，不等第一个周期：
    /// 否则启动后状态栏要闪一阵假的"PLC离线"才变成真实状态
    /// （默认周期 100ms，但 TCP 首次连接失败要 2s 才返回）。
    /// 这一次是在线程池上跑的，不会阻塞调用方（控制面板按钮）。
    /// </para>
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) == 0)
        {
            _timer.Start();
            // 立刻试连一次（线程池上跑，不阻塞调用方）：不等第一个周期，
            // 否则状态栏会先闪一段假的"PLC离线"。
            Task.Run(PollOnce);
        }
    }

    /// <summary>停止轮询。不等待在途回调 —— 已进入 <see cref="PollOnce"/> 的那一次会跑完。</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) == 1) _timer.Stop();
    }

    /// <summary>
    /// 手动触发一次（PLC 模式下界面按钮仍可用）。
    ///
    /// <para>
    /// 与轮询路径的区别：不读也不清 D100（没有 PLC 请求可应答），不经过 <c>_busy</c> 守卫，
    /// 也不 catch 订阅方异常。所以它可能与轮询触发并发，来源会如实记成 Manual。
    /// </para>
    /// </summary>
    public void Fire() => Triggered?.Invoke(TriggerOrigin.Manual);

    /// <summary>
    /// 停止并释放定时器。**不 Dispose <see cref="_plc"/>** —— 连接是组合根的单例，结果回写还要用。
    /// 重复调用安全。
    /// </summary>
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
