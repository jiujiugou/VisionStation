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
/// </summary>
public sealed class AuditService : IAuditService
{
    /// <summary>队列容量。一天几十条的量级，1024 已经够应付任何突发</summary>
    private const int QueueCapacity = 1024;

    private readonly IAuditRepository _repo;
    private readonly ILogger<AuditService>? _logger;

    /// <summary>
    /// 取当前用户名 / 角色。
    ///
    /// 用委托而不是直接读会话：阶段 1 还没有登录模块，这里回落到 Windows 账户；
    /// 阶段 2 接入 ISessionService 后，组合根换一行委托即可，所有埋点不用改。
    /// </summary>
    private readonly Func<(string UserName, string UserRole)> _identity;

    private readonly Channel<AuditEntry> _queue;
    private readonly Task _consumer;
    private readonly CancellationTokenSource _cts = new();
    private int _consecutiveFailures;
    private int _dropped;
    private int _disposed;

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
