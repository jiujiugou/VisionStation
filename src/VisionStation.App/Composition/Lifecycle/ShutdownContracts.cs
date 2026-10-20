namespace VisionStation.App.Composition.Lifecycle;

/// <summary>
/// 关闭六步的实现口（W-21）。
///
/// 拆出来的理由：Coordinator 只该负责"顺序、超时、异常隔离、幂等"这几件判定的事，
/// 不该认识 ServiceProvider 或具体设备。把六步做成接口后，测试用一个记录调用字符串的替身
/// 就能断言顺序，不必 mock 半个容器。
///
/// 步骤编号与 <c>docs/WPF开发规范.md</c> WPF-L1.4 一一对应，不许调换：
///   1 停触发 → 2 取消 → 3 等在途 → 4 释放 → 5 flush 日志 → 6 通知看门狗。
/// 顺序颠倒的后果：先释放容器再取消，在途帧会落在已释放的 DbContext / 相机句柄上。
/// </summary>
public interface IShutdownStages
{
    /// <summary>1. 停止接收新的触发（定时器停表）。</summary>
    void StopTrigger();

    /// <summary>2. 取消应用级令牌，让已排队但尚未开始的触发被拒绝。</summary>
    void CancelAppLifetime();

    /// <summary>
    /// 3. 等待在途帧/后台任务收尾。
    /// 返回 true = 已收尾；false = 超时（调用方必须如实记录，不能当成功）。
    /// </summary>
    Task<bool> WaitForIdleAsync(TimeSpan timeout);

    /// <summary>4. 释放设备与 DI 容器。实现方负责切到后台线程（见设计文档 §5.2）。</summary>
    Task DisposeServicesAsync();

    /// <summary>5. 日志刷盘。</summary>
    void FlushLog();

    /// <summary>6. 通知看门狗"这是正常退出"，避免被当成崩溃拉起。</summary>
    void NotifyWatchdog();
}

/// <summary>单步结果。失败要带错误信息，不能只有一句"关闭完成"。</summary>
public sealed record ShutdownStepResult(string Name, bool Ok, TimeSpan Elapsed, string? Error);

/// <summary>
/// 关闭总结果。
/// <paramref name="Completed"/> = 六步全部执行且无异常；
/// <paramref name="TimedOut"/> = 预算耗尽、有步骤被跳过或让步。
/// 两者都是 false 之外的状态必须写进日志 —— 不能把超时当成功。
/// </summary>
public sealed record ShutdownResult(bool Completed, bool TimedOut, IReadOnlyList<ShutdownStepResult> Steps);

/// <summary>关闭编排器。</summary>
public interface IShutdownCoordinator
{
    /// <summary>按固定顺序收尾。幂等：第二次调用返回第一次的同一个 Task，不重复执行。</summary>
    Task<ShutdownResult> StopAsync(TimeSpan timeout);
}

/// <summary>
/// 日志收尾。Serilog 是静态 API，抽一层才能让"第 5 步是否执行"可断言。
/// </summary>
public interface ILogShutdown
{
    void CloseAndFlush();
}

/// <summary>
/// 看门狗"正常退出"信号。
/// 语义要求：只有启动走完的正常关闭才发；启动失败不发（否则看门狗不再重试）。
/// </summary>
public interface IWatchdogSignal
{
    void NotifyCleanExit();
}

/// <summary>无看门狗的宿主（测试、无 Watchdog.exe 的部署）用这个，什么都不做。</summary>
public sealed class NoopWatchdogSignal : IWatchdogSignal
{
    public void NotifyCleanExit() { }
}
