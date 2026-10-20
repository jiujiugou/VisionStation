using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace VisionStation.App.Composition.Lifecycle;

/// <summary>
/// 关闭编排器 —— 只做四件事：顺序、超时、异常隔离、幂等。
///
/// 不在这里做的事：不认识 ServiceProvider、不认识相机、不认识 WPF。
/// 所有真实动作由 <see cref="IShutdownStages"/> 提供，所以本类可以用纯假对象测。
///
/// 判定规则（每一条都有对应测试）：
///   1. 顺序固定，不因某步失败而跳过后续（C5 / C7）；
///   2. 每步只拿到"总预算 - 已花时间"，单步再用 <c>WaitAsync</c> 封顶，防止一步吃掉全部预算之外的时间；
///   3. 预算 <= 0 时不做任何动作并记 TimedOut，不假装还在收尾；
///   4. 幂等：第二次调用返回第一次的同一个 Task，绝不重复执行步骤（C6）。
/// </summary>
public sealed class ShutdownCoordinator : IShutdownCoordinator
{
    private const string StopTriggerStep = "stop-trigger";
    private const string CancelLifetimeStep = "cancel-lifetime";
    private const string WaitIdleStep = "wait-idle";
    private const string DisposeServicesStep = "dispose";
    private const string FlushLogStep = "flush";
    private const string WatchdogStep = "watchdog";

    private readonly IShutdownStages _stages;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private Task<ShutdownResult>? _stopTask;

    public ShutdownCoordinator(IShutdownStages stages, ILogger? logger = null)
    {
        _stages = stages ?? throw new ArgumentNullException(nameof(stages));
        _logger = logger;
    }

    public Task<ShutdownResult> StopAsync(TimeSpan timeout)
    {
        lock (_gate)
        {
            return _stopTask ??= StopCoreAsync(timeout);
        }
    }

    private async Task<ShutdownResult> StopCoreAsync(TimeSpan timeout)
    {
        var budget = Stopwatch.StartNew();
        var steps = new List<ShutdownStepResult>(6);
        var timedOut = false;

        // 单步执行：预算检查 → 限时执行 → 记结果。失败一律吞进 Steps，不向调用方抛。
        async Task RunAsync(string name, Func<Task> action)
        {
            var remaining = timeout - budget.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                timedOut = true;
                _logger?.LogWarning("[关闭] 预算已用完，跳过 {Step}", name);
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                await action().WaitAsync(remaining).ConfigureAwait(false);
                steps.Add(new ShutdownStepResult(name, true, sw.Elapsed, null));
                _logger?.LogDebug("[关闭] {Step} 完成 {Elapsed}ms", name, sw.ElapsedMilliseconds);
            }
            catch (TimeoutException ex)
            {
                timedOut = true;
                steps.Add(new ShutdownStepResult(name, false, sw.Elapsed,
                    $"超时（>{remaining.TotalMilliseconds:0}ms）"));
                _logger?.LogWarning(ex, "[关闭] {Step} 超时，继续后续步骤", name);
            }
            catch (Exception ex)
            {
                // C7：单步失败不能阻断后续 —— 否则相机句柄留在那里，下次启动就报"设备被占用"
                steps.Add(new ShutdownStepResult(name, false, sw.Elapsed, ex.Message));
                _logger?.LogError(ex, "[关闭] {Step} 失败，继续后续步骤", name);
            }
        }

        // 第 3 步单独写：它的"失败"是返回 false（在途没收尾），不是抛异常。
        async Task RunWaitIdleAsync()
        {
            var remaining = timeout - budget.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                timedOut = true;
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                var idle = await _stages.WaitForIdleAsync(remaining).WaitAsync(remaining).ConfigureAwait(false);
                steps.Add(new ShutdownStepResult(WaitIdleStep, idle, sw.Elapsed,
                    idle ? null : "在途工作未在预算内收尾"));

                // 成功也要留一条：走查判据要求日志里 "wait-idle ok/timeout" 必有其一，
                // 只记失败等于"没看到就是好"，那是恒真判据。
                _logger?.LogInformation("[关闭] 在途收尾: {Result}（{Elapsed}ms）",
                    idle ? "ok" : "timeout", sw.ElapsedMilliseconds);

                if (!idle) timedOut = true;
            }
            catch (TimeoutException)
            {
                timedOut = true;
                steps.Add(new ShutdownStepResult(WaitIdleStep, false, sw.Elapsed,
                    $"超时（>{remaining.TotalMilliseconds:0}ms）"));
            }
            catch (Exception ex)
            {
                steps.Add(new ShutdownStepResult(WaitIdleStep, false, sw.Elapsed, ex.Message));
            }
        }

        await RunAsync(StopTriggerStep, () => { _stages.StopTrigger(); return Task.CompletedTask; })
            .ConfigureAwait(false);
        await RunAsync(CancelLifetimeStep, () => { _stages.CancelAppLifetime(); return Task.CompletedTask; })
            .ConfigureAwait(false);
        await RunWaitIdleAsync().ConfigureAwait(false);
        await RunAsync(DisposeServicesStep, _stages.DisposeServicesAsync).ConfigureAwait(false);

        // 摘要必须写在 flush 之前：第 5 步一执行，日志 sink 就关闭了，之后写的全部丢失。
        // 这也是"关了但没留痕"的一种典型形态 —— 顺序错了，日志看起来一切正常，实际只有前半段。
        WriteSummary(steps, budget.Elapsed, timedOut);

        await RunAsync(FlushLogStep, () => { _stages.FlushLog(); return Task.CompletedTask; })
            .ConfigureAwait(false);
        await RunAsync(WatchdogStep, () => { _stages.NotifyWatchdog(); return Task.CompletedTask; })
            .ConfigureAwait(false);

        var completed = !timedOut && steps.Count == 6 && steps.TrueForAll(s => s.Ok);
        return new ShutdownResult(completed, timedOut, steps);
    }

    private void WriteSummary(List<ShutdownStepResult> steps, TimeSpan elapsed, bool timedOut)
    {
        if (_logger == null) return;

        var failed = steps.Where(s => !s.Ok).ToList();

        if (failed.Count == 0 && !timedOut)
        {
            _logger.LogInformation("[关闭] 前四步完成（{Steps}），总耗时 {Elapsed}ms",
                string.Join(" ", steps.Select(s => $"{s.Name}={s.Elapsed.TotalMilliseconds:0}ms")),
                elapsed.TotalMilliseconds);
            return;
        }

        _logger.LogWarning("[关闭] 未完整收尾（超时={TimedOut}），失败步骤: {Failed}；总耗时 {Elapsed}ms",
            timedOut,
            string.Join("; ", failed.Select(s => $"{s.Name}:{s.Error}")),
            elapsed.TotalMilliseconds);
    }
}
