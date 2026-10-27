using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using VisionStation.Core.Models;
using VisionStation.Service.Audit;
using VisionStation.Service.Security;

namespace VisionStation.App.Services;

/// <summary>
/// 会话桥 —— 把服务层的会话事件搬到 UI 线程，并补记"系统自动发生"的会话审计。
///
/// 为什么需要它：
///   1) 线程。降级与班次登出由 <see cref="SessionService"/> 内部的定时器触发，
///      那是后台线程。ViewModel 直接订阅那个事件，就是在后台线程改绑定了 UI 的属性 ——
///      轻则界面不刷新，重则抛"该对象归另一个线程所有"。
///   2) 审计归属。用户主动登出的审计由发起命令记（否则不知道是谁点的"退出"），
///      而超时降级/超时登出没有人发起，必须在这里记。靠 <c>IsAutomatic</c> 区分，
///      两边各记各的，不会重复也不会漏。
///
/// 它自己是内部类、住在 App 层：Service 层不该知道 WPF 的 Dispatcher 存在。
/// </summary>
internal sealed class SessionBridge : IDisposable
{
    private readonly ISessionService _session;
    private readonly IAuditService _audit;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<SessionBridge>? _logger;
    private int _disposed;

    /// <summary>
    /// 必须在 UI 线程构造（<see cref="AppHost"/> 在启动阶段解析它时就在 UI 线程）。
    /// 构造里就订阅会话事件 —— 也就是说，**没人解析它，就等于没人订阅**，
    /// 这也是 AppHost 启动时显式取一次的原因。
    /// </summary>
    public SessionBridge(
        ISessionService session,
        IAuditService audit,
        ILogger<SessionBridge>? logger = null)
    {
        _session = session;
        _audit = audit;
        _logger = logger;

        // 构造发生在 UI 线程（组合根初始化时），此刻取 Dispatcher 是准确的。
        // 不用 SynchronizationContext：它的语义依赖当前线程有没有装上下文，
        // 而这里要的是"永远投到主界面线程"这一件确定的事。
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _session.Changed += OnSessionChanged;
    }

    /// <summary>
    /// 会话变化（可能在后台线程触发）。已在 UI 线程就直接处理，否则投递过去 ——
    /// 用 BeginInvoke 而不是 Invoke：会话变化的发起方（定时器）不该被 UI 卡住，
    /// 而且这里没有任何"等结果"的需求。
    /// </summary>
    private void OnSessionChanged(object? sender, SessionChangedEventArgs e)
    {
        if (_dispatcher.CheckAccess())
            Publish(e);
        else
            _dispatcher.BeginInvoke(new Action(() => Publish(e)));
    }

    /// <summary>
    /// UI 线程上：补审计（仅自动变化）+ 广播给界面。
    ///
    /// 两件事都包在 try 里：会话本身已经变了，通知发不出去不能反过来把会话改回去。
    /// </summary>
    private void Publish(SessionChangedEventArgs e)
    {
        try
        {
            // 只有系统自动触发的变化在这里记审计。
            // 主动登出/登录/改密都由各自的服务或命令记过了。
            if (e.IsAutomatic)
            {
                var who = e.PreviousUser ?? e.User;
                _audit.RecordAs(
                    who?.UserName ?? "unknown",
                    who?.RoleText ?? "未登录",
                    AuditAction.SessionExpired,
                    target: e.Kind == SessionChangeKind.Downgraded ? "降级" : "登出",
                    detail: e.Reason);
            }

            WeakReferenceMessenger.Default.Send(new SessionChangedMessage(
                e.User, e.EffectiveRole, e.Kind, e.Reason));
        }
        catch (Exception ex)
        {
            // 会话通知失败不能反过来影响会话本身
            _logger?.LogWarning(ex, "[会话] 分发会话变化失败");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _session.Changed -= OnSessionChanged;
    }
}
