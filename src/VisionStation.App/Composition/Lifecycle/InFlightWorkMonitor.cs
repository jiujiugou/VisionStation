namespace VisionStation.App.Composition.Lifecycle;

/// <summary>
/// 在途工作计数 —— 关闭时要能回答"还有没有一帧在跑"。
///
/// 这是本片唯一值得单独成类的东西：它有真实的并发语义（租约、归零唤醒、超时），
/// 而且没有它就只能 sleep 一段再猜。其余关闭步骤都是"走一遍就没了"的直线逻辑，
/// 不需要为可测再造接口。
///
/// 实现要点：
///   - 归零唤醒用 TaskCompletionSource + RunContinuationsAsynchronously：
///     否则释放租约的那条线程（可能是 UI 线程）会去跑等待者的 continuation；
///   - 唤醒动作在锁外执行；
///   - 等待用 Task.WaitAsync，超时不占线程（关窗处理器跑在 UI 线程上，这里一旦阻塞就是死等）。
/// </summary>
public sealed class InFlightWorkMonitor
{
    private readonly object _gate = new();
    private int _count;
    private TaskCompletionSource? _idle;

    public int Count => Volatile.Read(ref _count);

    /// <summary>登记一件在途工作。返回的租约必须 Dispose，否则关闭会等到超时。</summary>
    public IDisposable Begin()
    {
        lock (_gate)
        {
            _count++;
        }

        return new Lease(this);
    }

    /// <summary>等待计数归零。归零返回 true，超时返回 false。</summary>
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
                _idle = null;   // 下一轮在途要拿新的 TCS
            }
        }

        toComplete?.TrySetResult();
    }

    /// <summary>租约。重复 Dispose 只生效一次，避免计数减成负数。</summary>
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
