namespace VisionStation.App.Composition.Lifecycle;

/// <summary>
/// 应用级生命周期信号（W-21）。
///
/// 存在的理由：关闭流程需要一个"停止接收新工作"的单一事实源。
/// 不能靠 bool 字段 + 各处自己判断 —— 那种写法在关窗瞬间总有某个线程读到旧值。
///
/// 契约（对应设计文档 §0 的 C2）：
///   1. <see cref="BeginStop"/> 幂等，重复调用不重复取消、不抛异常；
///   2. 一旦 BeginStop 返回，<see cref="IsStopping"/> 必须立刻为 true（调用方据此拒绝新触发）；
///   3. <see cref="Stopping"/> 的取消状态不可回滚（不能"关闭取消后又继续跑"）。
/// </summary>
public interface IAppLifetime
{
    /// <summary>已开始关闭的信号。后台工作应轮询此令牌并尽早退出。</summary>
    CancellationToken Stopping { get; }

    /// <summary>关闭是否已开始。为 true 后不得再接收新的检测触发。</summary>
    bool IsStopping { get; }

    /// <summary>开始关闭（幂等）。</summary>
    void BeginStop();
}

/// <summary>
/// <see cref="IAppLifetime"/> 的实现。
///
/// <see cref="IsStopping"/> 用独立的整型标志而不是读 <see cref="CancellationToken.IsCancellationRequested"/>：
/// 令牌一旦随 CTS 释放就不能再碰（ObjectDisposedException），而"是否已停止"这个问题
/// 在释放之后仍然必须能安全回答。标志位只增不减，所以不存在回滚。
/// </summary>
public sealed class AppLifetime : IAppLifetime, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private int _stopped;

    public CancellationToken Stopping => _cts.Token;

    public bool IsStopping => Volatile.Read(ref _stopped) == 1;

    public void BeginStop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放过：取消状态早已成立，这里无事可做
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
