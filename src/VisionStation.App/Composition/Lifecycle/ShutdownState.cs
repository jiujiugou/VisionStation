namespace VisionStation.App.Composition.Lifecycle;

/// <summary>
/// 关闭状态 —— 一个 bool 加一个取消令牌，没有接口。
///
/// WPF 应用只有一个关闭入口（OnExit / 窗口 Closing），不值得为它造
/// "应用生命周期服务"。这里只做两件事：让后台工作能拿到取消信号，
/// 让触发入口能问一句"是不是已经在关了"。
///
/// 为什么用独立的整型标志而不是直接读令牌：令牌随 CTS 释放后就不能再碰
/// （ObjectDisposedException），而"是否已停止"在释放之后仍必须能安全回答。
/// </summary>
public sealed class ShutdownState : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private int _stopping;

    /// <summary>
    /// 取消令牌。**只在 <see cref="BeginStop"/> 之后才会被取消**。
    /// 注意：不要在 <see cref="Dispose"/> 之后访问它（会 ObjectDisposedException），
    /// 需要"是否已停"的判断请用 <see cref="IsStopping"/>。
    /// </summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// 是否已进入关闭流程。用独立的整型标志而不是读令牌状态，
    /// 就是为了释放之后仍能安全回答这个问题（见类型注释）。
    /// </summary>
    public bool IsStopping => Volatile.Read(ref _stopping) == 1;

    /// <summary>开始关闭。幂等：重复调用不重复取消、不抛异常。</summary>
    public void BeginStop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 1) return;

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放：取消状态早已成立
        }
    }

    /// <summary>释放 CTS。调用后 <see cref="IsStopping"/> 仍可安全读取。</summary>
    public void Dispose()
    {
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
