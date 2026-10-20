namespace VisionStation.App.Composition.Lifecycle;

/// <summary>
/// 在途工作计数（W-21）。
///
/// 存在的理由：关闭时要回答"还有没有一帧在跑"，必须有可观测的计数。
/// 之前的做法只有 <c>MainViewModel._isProcessing</c>（私有 bool），外部无从得知，
/// 关闭流程只能 sleep 一段再猜 —— 那既不可验证，也会把正常情况拖成超时。
///
/// 契约（对应设计文档 §0 的 C3）：
///   1. <see cref="Begin"/> 返回租约，租约 Dispose 时计数 -1 并唤醒等待者；
///   2. 计数归零时所有等待中的 <see cref="WhenIdleAsync"/> 必须完成（返回 true）；
///   3. <see cref="WhenIdleAsync"/> 必须真异步：禁止 Thread.Sleep / Task.Wait / SpinWait，
///      否则从 UI 线程等待会占死消息泵，在途帧的 continuation 永远排不上（设计文档 §5.1）；
///   4. 超时返回 false，不抛异常。
/// </summary>
public interface IInFlightWorkMonitor
{
    /// <summary>当前在途工作数量（诊断与断言用）。</summary>
    int Count { get; }

    /// <summary>登记一件在途工作。返回的租约必须 Dispose，否则关闭会一直等到超时。</summary>
    IDisposable Begin();

    /// <summary>等待计数归零。归零返回 true，超时返回 false。</summary>
    Task<bool> WhenIdleAsync(TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IInFlightWorkMonitor"/> 的实现。
///
/// 实现要点：
///   - 归零唤醒用 <see cref="TaskCompletionSource{TResult}"/> + <c>RunContinuationsAsynchronously</c>，
///     否则释放租约的那条线程（可能是 UI 线程）会去跑等待者的 continuation，把关闭流程反灌回工作线程；
///   - 唤醒动作放在锁外，避免锁内执行外部代码；
///   - 等待用 <c>Task.WaitAsync</c> 而不是轮询，超时不会占用线程。
/// </summary>
public sealed class InFlightWorkMonitor : IInFlightWorkMonitor
{
    private readonly object _gate = new();
    private int _count;
    private TaskCompletionSource? _idle;

    public int Count => Volatile.Read(ref _count);

    public IDisposable Begin()
    {
        lock (_gate)
        {
            _count++;
        }

        return new Lease(this);
    }

    public Task<bool> WhenIdleAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Task idleTask;

        lock (_gate)
        {
            if (_count == 0) return Task.FromResult(true);

            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            idleTask = _idle.Task;
        }

        return WaitAsync(idleTask, timeout, ct);
    }

    private static async Task<bool> WaitAsync(Task idleTask, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await idleTask.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void Release()
    {
        TaskCompletionSource? toComplete = null;

        lock (_gate)
        {
            if (_count > 0) _count--;

            if (_count == 0 && _idle != null)
            {
                toComplete = _idle;
                _idle = null;   // 下一轮在途要拿新的 TCS，不能复用已完成的
            }
        }

        toComplete?.TrySetResult();
    }

    /// <summary>
    /// 租约。重复 Dispose 只生效一次 —— 漏归还的后果是"关闭永远等到超时"，
    /// 所以宁可让重复释放变成无害操作，也不能让它把计数减成负数。
    /// </summary>
    private sealed class Lease : IDisposable
    {
        private InFlightWorkMonitor? _owner;

        public Lease(InFlightWorkMonitor owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release();
        }
    }
}
