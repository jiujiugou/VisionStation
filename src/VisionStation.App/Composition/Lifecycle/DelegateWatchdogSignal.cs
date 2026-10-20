namespace VisionStation.App.Composition.Lifecycle;

/// <summary>
/// 用委托实现的看门狗信号。
///
/// 存在的理由：是否该发"正常退出"信号，取决于 App 里的 <c>_startupCompleted</c> 门控
/// （启动失败不发，否则看门狗就不再重试）。把这个判断留在 App，用委托接进来，
/// 关闭流程就不必反向依赖 WPF 入口类。
/// </summary>
public sealed class DelegateWatchdogSignal : IWatchdogSignal
{
    private readonly Action _notify;

    public DelegateWatchdogSignal(Action notify) => _notify = notify ?? throw new ArgumentNullException(nameof(notify));

    public void NotifyCleanExit() => _notify();
}
