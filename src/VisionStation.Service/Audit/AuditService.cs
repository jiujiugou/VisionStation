using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VisionStation.Core.Models;
using VisionStation.Storage;

namespace VisionStation.Service.Audit;

/// <summary>
/// 审计写入契约。
///
/// <see cref="Record"/> 是同步的、永不抛异常、永不阻塞 —— 调用点在按钮命令里，
/// 一次磁盘 IO 的抖动不该让"停止检测"这个动作卡住。
/// </summary>
public interface IAuditService
{
    /// <summary>
    /// 记一条审计。不阻塞、不抛异常；队列满时丢并记 Error（丢审计比卡产线轻）。
    /// </summary>
    /// <param name="action">做了什么</param>
    /// <param name="target">对象（配方名 / 参数名 / 用户名）</param>
    /// <param name="before">修改前的值，只对修改类动作有意义</param>
    /// <param name="after">修改后的值</param>
    /// <param name="result">结果：成功 / 被拒绝 / 失败</param>
    /// <param name="detail">失败原因等补充说明</param>
    void Record(
        AuditAction action,
        string target = "",
        string? before = null,
        string? after = null,
        AuditResult result = AuditResult.Success,
        string? detail = null);

    /// <summary>
    /// 用指定身份记一条审计，不取当前会话。
    ///
    /// 专给登录流程用：登录成功的那一刻会话还没建立，登录失败更是根本没有会话，
    /// 但这两件事都必须记在"被尝试的那个账号"名下 ——
    /// 记成 Windows 账户等于把"谁在试密码"这条最重要的线索丢掉。
    /// </summary>
    void RecordAs(
        string userName,
        string userRole,
        AuditAction action,
        string target = "",
        string? before = null,
        string? after = null,
        AuditResult result = AuditResult.Success,
        string? detail = null);

    /// <summary>查询最近 N 条（供界面/排障）</summary>
    Task<IReadOnlyList<AuditEntry>> QueryRecentAsync(int count = 200);

    /// <summary>
    /// 按条件查询：动作 / 用户 / 起始时间，三者都可空。
    /// 审计页用它 —— 复盘时最常问的是"昨天下午谁动过参数"，不是"最近两百条"。
    /// </summary>
    Task<IReadOnlyList<AuditEntry>> QueryAsync(
        AuditAction? action = null,
        string? userName = null,
        DateTime? since = null,
        int count = 200);

    /// <summary>
    /// 关闭时把队列里剩下的写完，最多等指定时长。
    ///
    /// 不做成 IDisposable/IAsyncDisposable 是有意的：DI 容器在同步 Dispose 路径上
    /// 遇到只实现 IAsyncDisposable 的单例会抛异常，而这个程序的关闭流程是同步的。
    /// 让组合根显式调一次，比藏在生命周期钩子里更不容易踩空。
    /// </summary>
    void Flush(TimeSpan timeout);
}

/// <summary>
/// 审计服务 —— 把写入排到后台线程，主链路只做一个 <c>TryWrite</c>。
///
/// 为什么不直接写库：
///   1) 调用点全在 UI 线程的按钮命令里。SQLite 的 Default Timeout 是 5 秒，
///      一旦撞上管家清理的写锁，UI 会卡满 5 秒 —— 那一下正好是操作员按了急停。
///   2) 审计与检测记录共用一个库文件，写锁是同一把。检测每 200ms 一条，
///      审计不该挤进那条队列里。
///
/// 为什么队列满时丢最新而不是丢最旧：
///   丢最旧会静默抹掉"最早发生的那条"，而最早那条往往是因果链的起点
///   （先登录失败三次，然后才有人乱按）。丢最新 + 记 Error，至少日志里留痕。
///
/// 与 <see cref="Storage.InspectionRepository"/> 的分工：本类不认识 EF，
/// 只管排队和调度，落库交给 <see cref="IAuditRepository"/>。
///
/// <para>
/// <b>留痕范围（哪些动作必须记）</b>：本类不挑动作，动作由调用点决定。
/// 以 <c>AuditAction</c> 枚举为准，<b>已有真实埋点</b>的是 14 项，按性质分五组：
/// 启动/退出（AppStart、AppStop）；检测控制与安全（Start、Stop、EStopPressed）；
/// 变更类（ModeSwitch 切换模式、EngineReload 重载流程）；
/// 身份类（Login、Logout、LoginFailed、LoginLocked、SessionExpired）；
/// 账号管理（UserCreate、PasswordChange）。
/// 变更类应带 before/after —— 只记"谁动过"而不记改成什么，事后无法复现。
/// </para>
///
/// <para>
/// <b>已知的留痕缺口（读代码才能发现，不是设计）</b>：<c>EStopReset</c>、<c>RecipeSwitch</c>（换型）、
/// <c>ParameterChange</c>（改参数）、<c>UserDisable</c>（停用用户）四项在枚举、权限表与审计页的
/// 中文名表里都有，但<b>生产代码里没有任何 <c>Record</c> 调用点</b> ——
/// 也就是说这几类操作当前不会留下流水。补埋点时不需要动本类，加在各自的命令里即可。
/// </para>
///
/// <para>
/// <b>归属</b>：DI 单例，活到进程结束。构造即 <c>Task.Run</c> 起一个常驻消费循环
/// （单读者）；<see cref="Record"/> / <see cref="RecordAs"/> 由任意线程调用（多写者），
/// 内部无锁，靠 Channel 自身的并发语义。关闭时由组合根显式调 <see cref="Flush"/>
/// （不是 IDisposable，理由见接口注释），位置在容器释放之前。
/// </para>
///
/// <para>
/// <b>失败行为：永不抛、永不阻塞。</b>这是设计选择，不是疏漏 ——
/// 队列满了丢最新一条并记 Error；写库失败只记日志、该条审计就此消失，不重试也不回滚主流程。
/// 理由：审计是"旁证"，产线动作是"主证"。为了记一笔账而卡住停止检测/急停，
/// 是把安全让位于记录；反过来说，审计写不进去必须<b>在日志里看得见</b>（这就是那些 Error 的用途），
/// 否则就成了"以为在留痕、其实一条没落库"。
/// </para>
///
/// <para>
/// <b>已知副作用</b>：<see cref="Record"/> 读不到身份时会退化成 <c>unknown/unknown</c> 而不是丢掉这条 ——
/// 宁可记一笔"不知道是谁"，也好过这个动作完全没发生。
/// </para>
/// </summary>
public sealed class AuditService : IAuditService
{
    /// <summary>队列容量。一天几十条的量级，1024 已经够应付任何突发</summary>
    private const int QueueCapacity = 1024;

    /// <summary>落库端口。本类只调它的 SaveAsync / Query*，不碰 EF。</summary>
    private readonly IAuditRepository _repo;

    /// <summary>日志。可空：单测不验证日志时不必造 —— 但队列丢弃与写入失败只能靠它留痕。</summary>
    private readonly ILogger<AuditService>? _logger;

    /// <summary>
    /// 取当前用户名 / 角色。
    ///
    /// 用委托而不是直接注入 <c>ISessionService</c>：一是避免环（会话要审计、审计要读会话），
    /// 二是让"这次操作算谁的"变成一个可替换的输入。
    /// 现状：组合根传的委托读会话；未登录（含启动阶段的 AppStart）回落到 Windows 账户 + 角色"未登录"，
    /// 那本身就是有效信息（"这条是在没人登录时发生的"）。
    /// </summary>
    private readonly Func<(string UserName, string UserRole)> _identity;

    /// <summary>审计条目队列（有界 1024，满即丢最新）。写方任意线程，读方只有消费循环一个。</summary>
    private readonly Channel<AuditEntry> _queue;

    /// <summary>后台消费任务。由 <see cref="Flush"/> 等待它清空后结束。</summary>
    private readonly Task _consumer;

    /// <summary>取消源。只在 Flush 超时（还有条目没写完）时才用 —— 是"放弃等待"的开关，不是常规关闭路径。</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>连续写库失败次数。用于日志节流：表不存在时每 10 条才记一次，否则会把日志刷爆。</summary>
    private int _consecutiveFailures;

    /// <summary>累计丢弃条数（队列满）。只用于日志里的"累计丢弃 N 条"。</summary>
    private int _dropped;

    /// <summary>1 = Flush 已经走过。保证队列只 Complete 一次。</summary>
    private int _disposed;

    /// <summary>
    /// 构造即启动消费循环（<c>Task.Run</c>，跑在线程池上，直到 Flush 关掉队列）。
    ///
    /// 副作用：占一个常驻后台线程等待 Channel；解析这个单例就等于"开始记账"，
    /// 所以 <c>AppHost</c> 在启动末尾解析它并立刻记第一条 AppStart。
    /// 没有可失败的前置条件 —— 仓储连不上也照样构造成功，错误会在消费循环里逐条暴露。
    /// </summary>
    public AuditService(
        IAuditRepository repo,
        Func<(string UserName, string UserRole)>? identity = null,
        ILogger<AuditService>? logger = null)
    {
        _repo = repo;
        _logger = logger;

        _identity = identity ?? (() => (Environment.UserName, "未登录"));

        _queue = Channel.CreateBounded<AuditEntry>(new BoundedChannelOptions(QueueCapacity)
        {
            // Wait 模式下 TryWrite 满时直接返回 false（不阻塞），正好是要的语义
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>
    /// 记一条审计，身份取"当前会话"。
    ///
    /// 前置条件：无。线程：任意线程（调用点是 UI 线程按钮命令，但本方法不碰 UI）。
    /// 副作用：只往队列里塞一个对象，微秒级返回。
    /// 失败行为：不抛、不阻塞 —— 身份取不到就记成 unknown；队列满就丢并记 Error。
    /// </summary>
    public void Record(
        AuditAction action,
        string target = "",
        string? before = null,
        string? after = null,
        AuditResult result = AuditResult.Success,
        string? detail = null)
    {
        // 身份取不到不该丢审计：拿不到用户名也要记下"这个动作发生过"
        string userName, userRole;
        try
        {
            (userName, userRole) = _identity();
        }
        catch
        {
            userName = "unknown";
            userRole = "unknown";
        }

        RecordAs(userName, userRole, action, target, before, after, result, detail);
    }

    /// <summary>
    /// 用指定身份记一条审计。登录/登出/失败尝试必须走这里 ——
    /// 那些时刻"会话"要么还没建立、要么已经没了，用 <see cref="Record"/> 会把账记到 Windows 账户名下，
    /// 而"谁在试密码"恰恰是最该留住的信息。
    ///
    /// 空用户名/空角色会被补成 <c>unknown</c> 而不是丢弃：一条残缺的审计也胜过没有。
    /// 同样不抛：队列满时丢本条 + 记 Error（丢审计比卡产线轻）。
    /// </summary>
    public void RecordAs(
        string userName,
        string userRole,
        AuditAction action,
        string target = "",
        string? before = null,
        string? after = null,
        AuditResult result = AuditResult.Success,
        string? detail = null)
    {
        var entry = new AuditEntry
        {
            Timestamp = DateTime.Now,
            UserName = string.IsNullOrWhiteSpace(userName) ? "unknown" : userName,
            UserRole = string.IsNullOrWhiteSpace(userRole) ? "unknown" : userRole,
            Action = action,
            Target = target,
            Before = before,
            After = after,
            Result = result,
            Detail = detail,
        };

        if (_queue.Writer.TryWrite(entry))
            return;

        var n = Interlocked.Increment(ref _dropped);
        _logger?.LogError("[审计] 队列已满，丢弃一条：{Action} {Target}（累计丢弃 {Dropped} 条）",
            action, target, n);
    }

    public Task<IReadOnlyList<AuditEntry>> QueryRecentAsync(int count = 200)
        => _repo.QueryRecentAsync(count);

    public Task<IReadOnlyList<AuditEntry>> QueryAsync(
        AuditAction? action = null,
        string? userName = null,
        DateTime? since = null,
        int count = 200)
        => _repo.QueryAsync(action, userName, since, count);

    /// <summary>
    /// 后台消费循环。单条失败不影响后续 —— 审计是流，不能因为一条写不进去就整条流停摆。
    /// </summary>
    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    await _repo.SaveAsync(entry).ConfigureAwait(false);

                    if (_consecutiveFailures > 0)
                    {
                        _logger?.LogInformation("[审计] 写入恢复（此前连续失败 {Count} 次）", _consecutiveFailures);
                        Interlocked.Exchange(ref _consecutiveFailures, 0);
                    }
                }
                catch (Exception ex)
                {
                    // 连续失败时节流：第 1 次和每 10 次记一条，避免表不存在时刷爆日志。
                    var n = Interlocked.Increment(ref _consecutiveFailures);
                    if (n == 1 || n % 10 == 0)
                    {
                        _logger?.LogError(ex,
                            "[审计] 写入失败：{Action} {Target}（连续第 {Count} 次）", entry.Action, entry.Target, n);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭路径
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[审计] 消费循环异常退出，后续审计将不再落库");
        }
    }

    /// <summary>
    /// 关闭时等待队列清空。
    ///
    /// 不等的话，关机那一瞬间队列里剩下的几条会随进程消失 ——
    /// 而"关机前最后做了什么"恰恰是事后最想知道的。
    ///
    /// <para>
    /// 机制：先 <c>TryComplete</c> 让消费循环"读完剩余条目后自然退出"，再 <c>Wait(timeout)</c>。
    /// 超时不是错误，只是如实记一条警告，然后取消消费循环 —— 剩下的条目确实会丢，
    /// 但不允许为了记账把关闭流程拖到超时之外（<c>AppHost</c> 给的时间预算是严格的）。
    /// </para>
    /// <para>
    /// 幂等（<c>_disposed</c>），重复调用直接返回。**注意副作用**：调用之后队列已关闭，
    /// 之后再来的 <see cref="RecordAs"/> 会被 TryWrite 拒掉，走"队列已满"那条日志 ——
    /// 消息措辞与实际原因不符（其实是"已关闭"），排障时别被这句话带偏。
    /// 所以组合根把它放在关闭流程的倒数第二步（记完 AppStop 之后、释放容器之前）。
    /// </para>
    /// </summary>
    public void Flush(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _queue.Writer.TryComplete();

        try
        {
            if (!_consumer.Wait(timeout))
            {
                _logger?.LogWarning("[审计] 关闭时仍有 {Count} 条未落库（等待超时）", _queue.Reader.Count);
                _cts.Cancel();
            }
        }
        catch (AggregateException ex)
        {
            // 消费循环自身的异常已在其内部记过，这里只吞掉，不影响关闭
            _logger?.LogDebug(ex, "[审计] 关闭时消费线程异常已忽略");
        }

        _cts.Dispose();
    }
}
