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
/// </summary>
public sealed class VmResultListener : IDisposable
{
    /// <summary>单行上限。防畸形/无换行数据把内存吃光，正常结果远小于这个值。</summary>
    private const int MaxLineLength = 1024 * 1024;

    private readonly string _bindAddress;
    private readonly int _port;
    private readonly Action<string> _onLine;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();

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

    /// <summary>是否已开始监听</summary>
    public bool IsListening => _listener is not null;

    /// <summary>
    /// 最后收到一行结果的时刻（UTC）。null = 本次启动以来从未收到。
    ///
    /// 为什么要有这个：产线上"VM 崩了"和"产线停机没料"在界面上本是同一个黑屏。
    /// 有了这个时间，才能说出"已 N 秒没有结果"，也才能区分"从没通过"和"刚断"。
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
    /// 断流判定要它兜底：刚连上还没推第一件时，基准是"连上多久了"，
    /// 不能拿"上一次的结果时间"算 —— 那会把正常的第一件等待误判成断流。
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
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_listener is not null) return;

        var address = IPAddress.Parse(_bindAddress);
        _listener = new TcpListener(address, _port);
        _listener.Start();

        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        _logger?.LogInformation("[VM链路] 已监听 {Endpoint}，等待 VisionMaster 推送结果", Endpoint);
    }

    /// <summary>停止监听并断开当前连接。</summary>
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

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = "未知";

        // 连接计数与建立时刻：进方法就记，finally 里退。第一个连上的才开始计时，
        // 这样"连着但一直没结果"与"根本没连上"在监控侧是两件事。
        if (Interlocked.Increment(ref _clientCount) == 1)
            Interlocked.Exchange(ref _connectedAtUtcTicks, DateTime.UtcNow.Ticks);

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
            if (Interlocked.Decrement(ref _clientCount) == 0)
                Interlocked.Exchange(ref _connectedAtUtcTicks, 0);

            _logger?.LogInformation("[VM链路] 连接已结束：{Remote}", remote);
        }
    }

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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        try { _cts.Dispose(); }
        catch (ObjectDisposedException) { /* 重复释放 */ }
    }
}
