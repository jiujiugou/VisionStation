using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace VisionStation.Devices.Vm;

/// <summary>
/// 监听 VisionMaster 推来的检测结果（TCP 中转模式的接收端）。
///
/// 为什么是"上位机做服务端、VM 做客户端"：
///   VM 的「发送数据」模块配成 TCP 客户端连一个地址就行 —— 这是 VM 侧配置最少的一种形态。
///   反过来（上位机连 VM）要在 VM 里配服务端并处理并发，配置更绕，收益一样。
///
/// 协议：**一行一条 JSON**。按行读是最省事的定界方式，日志里也直接能看懂；
/// 代价是结果里不能出现真实换行（JSON 序列化本来就保证这一点）。
///
/// 本类只负责"把行交出去"，不认识 JSON 也不认识 InspectionReport ——
/// 解析在 Engines.Vm 的 VmResultParser 里，那一层纯托管、可单测。
///
/// <para>
/// <b>定位</b>：整条 VM 链路的最末端。上游是 VisionMaster 的「发送数据」模块（TCP 客户端），
/// 下游是构造时注入的 <c>onLine</c> 回调 —— 组合根里接的是
/// <c>VmInspectionDriver.OnLine</c>（解析 → 入库 → 回写 PLC → 广播界面）。
/// 本类自己不做任何业务判断，所以它可以在没有 WPF、没有数据库的测试里单独跑。
/// </para>
///
/// <para>
/// <b>归属</b>：由 <c>VmInspectionDriver</c> 在构造时 new（一个驱动一个监听器），
/// 驱动本身是 DI 单例，所以它活到进程结束。启停由 <c>AppHost</c> 控制：
/// 启动在 InitializeAsync 的倒数第二步（**必须晚于建表**，否则第一帧结果来了没地方写），
/// 停止在关闭流程第 1.5 步（**必须早于释放容器**，否则在途帧会落到已释放的 DbContext 上）。
/// </para>
///
/// <para>
/// <b>线程</b>：
/// <list type="bullet">
///   <item><see cref="Start"/> 在 UI 线程调用（只做绑定端口 + 起 Task，不阻塞）；</item>
///   <item><see cref="AcceptLoopAsync"/> 跑在线程池，接受连接后**每个客户端再起一个 Task**；</item>
///   <item>读数循环在各自的任务上跑，<c>onLine</c> 是**同步调用**的 —— 也就是说回调慢会直接
///         拖慢这一条连接的读取。<c>VmInspectionDriver.OnLine</c> 里是同步等入库（<c>GetAwaiter().GetResult()</c>），
///         所以"数据库慢"会表现为"VM 侧发送变慢"，而不是这里堆积。多客户端时才会真有并发。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>失败行为</b>：
/// <list type="bullet">
///   <item><b>启动失败直接抛</b>（端口被占用 → <see cref="System.Net.Sockets.SocketException"/>；
///         地址写错 → <see cref="FormatException"/>）。不降级、不重试：现场"起了但没在听"
///         比启动失败难查一百倍，宁可启动期就红。异常会一路冒到 <c>AppHost.InitializeAsync</c>，
///         结果是本次启动失败并写 crash.log。</item>
///   <item><b>运行期断线只记日志</b>：网线拔了、VM 关了、机器重启，都按"连接结束"处理，监听循环继续等下一个客户端。</item>
///   <item><b>回调抛异常只记日志</b>：一次数据库异常不能让整条链路哑掉直到重启。</item>
/// </list>
/// </para>
/// </summary>
public sealed class VmResultListener : IDisposable
{
    /// <summary>
    /// 单行上限。防畸形/无换行数据把内存吃光，正常结果远小于这个值。
    ///
    /// <para>
    /// ⚠ <b>它是在 <c>ReadLineAsync</c> 返回之后才检查的</b>：也就是说一行已经读完（内存已经分配过）才判超长。
    /// 对"发了一堆字节但**永远不发换行**"的连接，这个检查永远不会触发，内存仍然会涨 ——
    /// 真正兜住这种情况的是"客户端只有一个、且是可信任的 VM"这个前提。
    /// 要硬兜得在读取层限长（例如按块读并自己找换行），那是另一套实现，现在没做。
    /// 这里的作用是：**格式串漏了换行的故障能被看见**（否则会表现为"一条巨大的行，解析失败"）。
    /// </para>
    /// </summary>
    private const int MaxLineLength = 1024 * 1024;

    private readonly string _bindAddress;
    private readonly int _port;
    private readonly Action<string> _onLine;
    private readonly ILogger? _logger;
    /// <summary>
    /// 当前这一轮的取消源。**每次 <see cref="Start"/> 都会换一个新的**（F-62）。
    ///
    /// <para>
    /// <b>为什么不能是 readonly 单次创建</b>：<see cref="CancellationTokenSource.Cancel"/>
    /// 不可撤销。Stop 之后若继续沿用同一个源，第二次 Start 里
    /// <c>Task.Run(action, token)</c> 收到已取消的 token 会直接返回一个 Canceled 任务、
    /// <b>委托一次都不执行</b>（BCL 约定），而端口已经绑上 ——
    /// 表现为"IsListening 为 true、连接能建立、数据全丢、无异常无日志"。
    /// </para>
    /// </summary>
    private CancellationTokenSource _cts = new();

    private TcpListener? _listener;
    private Task? _acceptTask;
    private volatile bool _disposed;

    /// <summary>
    /// 最后收到一行结果的时刻（UTC ticks）。0 = 本次启动以来从未收到。
    /// 用 ticks 而不是 DateTime 是为了 Interlocked —— 接收线程写、监控线程读，中间不能撕裂。
    /// </summary>
    private long _lastLineUtcTicks;

    /// <summary>当前连接建立时刻（UTC ticks），0 = 没有连接。多客户端时取第一个连上的。</summary>
    private long _connectedAtUtcTicks;

    /// <summary>当前连着的客户端数（VM 断线重连时可能短暂并存）</summary>
    private int _clientCount;

    /// <param name="bindAddress">绑定地址，如 127.0.0.1 或 0.0.0.0</param>
    /// <param name="port">监听端口</param>
    /// <param name="onLine">每收到一行调用一次。回调抛异常不会中断监听</param>
    /// <param name="logger">可选日志</param>
    public VmResultListener(string bindAddress, int port, Action<string> onLine, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindAddress);
        ArgumentNullException.ThrowIfNull(onLine);

        _bindAddress = bindAddress;
        _port = port;
        _onLine = onLine;
        _logger = logger;
    }

    /// <summary>监听地址，用于界面与自检显示</summary>
    public string Endpoint => $"{_bindAddress}:{Port}";

    /// <summary>
    /// 实际监听端口。构造时传 0 表示由系统分配（测试用），启动后才能拿到真实值。
    /// </summary>
    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _port;

    /// <summary>是否已开始监听。**只看字段是否被赋值，不代表端口真的绑定成功**，见 <see cref="Start"/> 的说明。</summary>
    public bool IsListening => _listener is not null;

    /// <summary>
    /// 最后收到一行结果的时刻（UTC）。null = **本轮连接**以来从未收到。
    ///
    /// 为什么要有这个：产线上"VM 崩了"和"产线停机没料"在界面上本是同一个黑屏。
    /// 有了这个时间，才能说出"已 N 秒没有结果"，也才能区分"从没通过"和"刚断"。
    ///
    /// <para>
    /// <b>重连时会被清零</b>（2026-10-27 改的口径，原来刻意保留旧值）：
    /// 断流判定的基准是 <c>LastLineUtc ?? ConnectedAtUtc</c>，如果保留上一轮的时间戳，
    /// 这个 <c>??</c> 永远轮不到兜底 —— 重连后到首帧之间，基准是上一次连接时代的旧值，
    /// 只要断开时间超过阈值，重连后第一秒就会记一条"已 N 秒没有收到 VM 结果"的 Error，
    /// 首帧一到又自己恢复。运行中凭空多一条 Error，比一个跨连接的秒数更值得避免。
    /// </para>
    /// <para>
    /// 代价：重连后到首帧到达之前，界面"距上次结果多久"显示成"从未收到"。
    /// 语义只有两种可选（跨连接的旧值 / 本轮连接的真相），这里选了后者。
    /// </para>
    /// </summary>
    public DateTime? LastLineUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastLineUtcTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>
    /// 当前这轮连接建立于何时（UTC）。null = 现在没有客户端连着。
    ///
    /// <para>
    /// <b>它存在的理由</b>：断流判定的基准取 <c>LastLineUtc ?? ConnectedAtUtc</c> ——
    /// "刚连上还没推第一件时，以连上多久为基准，不要拿上一次的结果时间算，
    /// 那会把正常的第一件等待误判成断流"。
    /// </para>
    ///
    /// <para>
    /// 这层兜底要真正生效，还依赖 <see cref="LastLineUtc"/> 在重连时被清零
    /// （见那边的说明）；否则它是上一轮的旧值，<c>??</c> 根本用不上。
    /// </para>
    /// <para>
    /// 已知取舍（未修，影响可忽略）：多客户端时只在**第一个**连上时记录时刻，最后一个断开时清 0。
    /// 连接计数与时刻分别由两次 Interlocked 完成，读侧（每秒一次的链路检查）可能读到
    /// "已连接，但时刻还没写进来"的瞬时组合 —— 判定侧把这种组合判为断流而不是健康
    /// （见 <see cref="VmLinkMonitor.Evaluate"/>），宁可误报一次也不挂绿灯。
    /// </para>
    /// </summary>
    public DateTime? ConnectedAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _connectedAtUtcTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>当前是否有 VisionMaster 连着</summary>
    public bool IsClientConnected => Volatile.Read(ref _clientCount) > 0;

    /// <summary>
    /// 开始监听。端口被占用等错误**直接抛出** ——
    /// 现场"起了但没在听"比启动失败难查一百倍，宁可启动期就红。
    ///
    /// <para>
    /// 前置条件：未 Dispose。重复调用是幂等的（<c>_listener != null</c> 直接返回）。
    /// </para>
    ///
    /// <para>
    /// <b>可重复启停</b>（F-62 修）：每次启动重建取消源、先绑定成功再发布字段，
    /// 所以 Stop → Start 能真正重新收数；绑定失败也不会留下"IsListening 为 true 但没在听"的假状态。
    /// 前提是调用方在 Stop 之后才 Start —— 并发 Start/Start 仍由 <c>_listener</c> 幂等分支兜住。
    /// </para>
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_listener is not null) return;

        var address = IPAddress.Parse(_bindAddress);

        // 先绑到局部变量，成功后再发布到字段（F-62 的同源缺陷）：
        // 绑定失败时字段必须保持 null，否则幂等分支会把"根本没在监听"挡回去，
        // IsListening 变成假绿，链路监控一直显示"等待 VisionMaster 连接"。
        var listener = new TcpListener(address, _port);
        listener.Start();

        // 每次启动换一个新令牌（F-62）：Stop 会把当前令牌 Cancel 且不可撤销，
        // 沿用旧令牌的后果见 _cts 字段说明。
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _cts, cts);
        try { previous.Cancel(); }
        catch (ObjectDisposedException) { /* 已在 Dispose 里释放 */ }
        try { previous.Dispose(); }
        catch (ObjectDisposedException) { /* 重复释放 */ }

        _listener = listener;
        _acceptTask = Task.Run(() => AcceptLoopAsync(cts.Token), cts.Token);
        _logger?.LogInformation("[VM链路] 已监听 {Endpoint}，等待 VisionMaster 推送结果", Endpoint);
    }

    /// <summary>
    /// 停止监听并断开当前连接。
    ///
    /// <para>
    /// <b>最多阻塞 2 秒</b>（等接受循环落地）。调用点在关闭流程第 1.5 步，那一刻用户的预期是
    /// "窗口马上消失"，所以这个上限是必要的 —— 等不到就放弃，如实留一条 Debug 日志。
    /// </para>
    /// <para>
    /// 断开的是"监听器"，不是已建立的客户端连接：客户端任务靠 <see cref="_cts"/> 取消，
    /// 它们的 Socket 由各自的 <c>using</c> 收尾。取消之后已经在 <c>onLine</c> 里的那一帧会跑完
    /// （这正是关闭流程第 3 步等在途帧要处理的部分）。
    /// </para>
    /// <para>
    /// <b>已 Dispose 时直接返回</b> —— 那时端口已经由 <see cref="Dispose"/> 亲自停过了，
    /// 这里不需要（也不应该）再停一次。
    /// </para>
    /// </summary>
    public void Stop()
    {
        if (_disposed) return;

        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* 已释放 */ }

        try { _listener?.Stop(); }
        catch (SocketException ex) { _logger?.LogDebug(ex, "[VM链路] 停止监听时套接字已关闭"); }

        _listener = null;

        // 显式传 None：这里必须等它落地，取消已经没有意义（token 上面刚 cancel 过）
        try { _acceptTask?.Wait(TimeSpan.FromSeconds(2), CancellationToken.None); }
        catch (AggregateException ex) { _logger?.LogDebug(ex, "[VM链路] 接收循环退出时带异常"); }

        _acceptTask = null;
    }

    /// <summary>
    /// 接受连接的循环（线程池）。**它自己不会因为单次失败退出** ——
    /// 只有取消或监听器被关闭才 return，其余异常一律记日志后继续。
    ///
    /// <para>
    /// 每个客户端单独起一个 Task（<c>_ = Task.Run(...)</c>）而不是 <c>await</c>：
    /// 这样一条连接上的慢处理不会挡住后续连接。代价是任务句柄被丢弃，
    /// 无法逐个等待/取消（只能靠共享的 <paramref name="ct"/>）—— 对"一个 VM、偶发重连"的规模足够。
    /// </para>
    /// <para>
    /// <c>_listener!</c> 的空断言是安全的：此任务只在 <see cref="Start"/> 里、赋值之后才启动，
    /// 而 <see cref="Stop"/> 会先取消再置 null；那时循环已经在下一次判断处退出。
    /// </para>
    /// </summary>
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                // Stop() 关掉了监听器，正常退出
                return;
            }
            catch (SocketException ex)
            {
                // 单次 accept 失败不该终止整个监听：VM 侧重连风暴时这里会抖
                _logger?.LogWarning(ex, "[VM链路] 接受连接失败，继续监听");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    /// <summary>
    /// 单个客户端的读取循环（每连接一个任务）。
    ///
    /// <para>
    /// <b>编码写死 UTF-8（无 BOM）</b>：VM 侧「发送数据」默认就是这个，改配置也应当保持。
    /// 如果现场发现中文标签变成乱码，先查 VM 那边的输出编码，不要在这里加 GBK 分支 ——
    /// 两种编码同时存在时，正确的那个也会被错误的那条路径搅乱。
    /// </para>
    /// <para>
    /// <b>按行读的三种换行都认</b>（<c>\r\n</c> / <c>\n</c> / <c>\r</c>），这是 <c>StreamReader.ReadLineAsync</c>
    /// 的行为，不是本类自己实现的 —— 所以 VM 侧配 <c>\r\n</c> 或 <c>\n</c> 都能用，不必在协议上手写约定。
    /// </para>
    /// <para>
    /// <b>退出路径</b>：对端关闭（读到 null）、取消、或三种断线异常。三种都只记一条日志，
    /// 不向上抛 —— 监听循环还在等下一个客户端，一条连接断了不是故障。
    /// </para>
    /// </summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = "未知";

        // 连接计数与建立时刻：进方法就记，finally 里退。
        //
        // **顺序是有意的：先立基准，再让"已连接"对外可见。**
        // 反过来（先 Increment 再写时刻）会出现一瞬间 IsClientConnected == true
        // 而 ConnectedAtUtc 还是 null、LastLineUtc 已经被清掉 —— 判定侧一个基准都拿不到。
        // 按现在的顺序，"已连接"一旦可见，基准必然已经就位。
        if (Volatile.Read(ref _clientCount) == 0)
        {
            Interlocked.Exchange(ref _connectedAtUtcTicks, DateTime.UtcNow.Ticks);

            // 重连时清掉上一轮的最后结果时刻 —— **这是"重连后首帧到达前不误报断流"的关键一步**。
            //
            // 不清的话，VmInspectionDriver 里的 `LastLineUtc ?? ConnectedAtUtc` 永远轮不到兜底：
            // 重连后基准是上一次连接时代的旧时间戳，只要断开时间超过阈值，
            // 重连后第一秒就判 Stalled 并记一条 Error，首帧一到又自己恢复（自愈式误报，
            // 半年后看到日志的人只会以为现场真的断过）。
            // 清成 null 之后，基准回落到 ConnectedAtUtc —— 也就是"这轮连接建立了多久"。
            //
            // 代价见 LastLineUtc 的注释：重连后界面"距上次结果多久"会显示"从未收到"。
            Interlocked.Exchange(ref _lastLineUtcTicks, 0);
        }

        Interlocked.Increment(ref _clientCount);

        try
        {
            remote = client.Client.RemoteEndPoint?.ToString() ?? "未知";
            _logger?.LogInformation("[VM链路] VisionMaster 已连接：{Remote}", remote);

            using (client)
            using (var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false), false, 8192, false))
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) break;          // 对端关闭
                    if (line.Length == 0) continue;

                    if (line.Length > MaxLineLength)
                    {
                        _logger?.LogError("[VM链路] 收到超长结果（{Length} 字符），已丢弃；检查 VM 侧格式串是否漏了换行",
                            line.Length);
                        continue;
                    }

                    // 时间戳必须在 Dispatch 之前记：Dispatch 里的异常被吞掉了，
                    // 但那确实是一条到达的结果 —— 链路是通的，只是处理失败。
                    Interlocked.Exchange(ref _lastLineUtcTicks, DateTime.UtcNow.Ticks);
                    Dispatch(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正在停止
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // 现场最常见的三种断线：网线拔了、VM 关了、机器重启。都不是故障，记一条继续等
            _logger?.LogWarning(ex, "[VM链路] 连接断开：{Remote}", remote);
        }
        finally
        {
            // 最后一个客户端走了才清建立时刻："no client" 时 ConnectedAtUtc 必须回到 null，
            // 否则下一轮重连的第一帧到达前，判定侧会拿上一轮的连接时刻当基准。
            if (Interlocked.Decrement(ref _clientCount) == 0)
                Interlocked.Exchange(ref _connectedAtUtcTicks, 0);

            _logger?.LogInformation("[VM链路] 连接已结束：{Remote}", remote);
        }
    }

    /// <summary>
    /// 把一行交给业务回调。**同步执行、就地吞异常**。
    ///
    /// <para>
    /// 同步是刻意的：回调里是"解析 → 入库 → 回写 PLC"，入库必须有序；
    /// 若改成 <c>Task.Run</c> 发出去，两帧就可能并发写同一个仓储。
    /// 代价是读取循环被回调拖住 —— 这正是 <c>VmInspectionDriver</c> 里那个 <c>_busy</c> 丢帧计数存在的原因。
    /// </para>
    /// <para>
    /// 吞异常也是刻意的：一次数据库异常如果逃出去，会带走这条连接的读取循环，
    /// 整条链路哑到重启程序为止 —— 而现场看到的只是"VM 明明在推、界面不动"。
    /// 记日志 + 继续接收，最坏是丢一条记录（并且日志里查得到）。
    /// </para>
    /// </summary>
    private void Dispatch(string line)
    {
        try
        {
            _onLine(line);
        }
        catch (Exception ex)
        {
            // 回调里是解析 + 入库 + 回 PLC，出问题不能把接收线程带走 ——
            // 否则一次数据库异常就会让整条链路哑掉，直到重启程序。
            _logger?.LogError(ex, "[VM链路] 处理结果时异常（已忽略，继续接收）");
        }
    }

    /// <summary>
    /// 释放：停监听 + 释放取消源。幂等。
    ///
    /// <para>
    /// <b>顺序不能反：先 <see cref="Stop"/>、后置 <see cref="_disposed"/>。</b>
    /// <see cref="Stop"/> 的第一行是 <c>if (_disposed) return;</c>，先置位的话这次 Stop
    /// 会被它自己的守卫挡回去 —— 监听器不关、端口占着直到进程退出，
    /// 表现就是"第二次启动报地址已在使用"。生产路径长期没暴露是因为
    /// <c>VmInspectionDriver.Dispose</c> 先显式调了 <c>Stop()</c>；
    /// 任何"只用 Dispose 收尾"的调用方（测试、工具、探针）都会踩到。
    /// </para>
    /// <para>
    /// 先 Stop 也顺带解决了 <see cref="_cts"/> 的释放时序：<c>Cancel()</c> 发生在 <c>Dispose()</c>
    /// 之前，接受循环持有的 <c>Token</c> 副本在释放那一刻**已经是已取消状态**，
    /// 取消回调也已经在 <c>Cancel()</c> 内部同步跑完了 ——
    /// 不会出现"Token 副本还在用，取消请求却发不出去"的窗口。
    /// </para>
    /// <para>
    /// <see cref="_disposed"/> 一旦为 true 本实例即报废：<see cref="Start"/> 抛
    /// <see cref="ObjectDisposedException"/>、<see cref="Stop"/> 变成空操作。
    /// 这是既有契约：Dispose 之后再 Start 没有意义（取消源已释放），不做"Dispose 后复活"。
    /// 并发调用 Dispose 最坏是重复跑一遍 Stop —— <c>Cancel()</c> / <c>TcpListener.Stop()</c> /
    /// <c>Wait</c> 都是可重入且不抛的，<c>_cts.Dispose()</c> 也由下方的 catch 兜住。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        // 先把监听真正停掉。此刻 _disposed 还是 false，Stop 的守卫才会放行。
        Stop();

        // 停完再置位：之后 Start 抛、Stop 空转、重复 Dispose 直接返回。
        _disposed = true;

        try { _cts.Dispose(); }
        catch (ObjectDisposedException) { /* 重复释放 */ }
    }
}
