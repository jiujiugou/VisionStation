using System.Diagnostics;

namespace VisionStation.App.Services.Restart;

/// <summary>
/// 请求重启本程序（F-69 一键自动重启）。实现方只做两件事：记下"要重启"，
/// 然后用<b>正常关闭路径</b>结束本进程——绝不直接 Kill。
/// 真正拉起后继进程的是 <c>App.OnExit</c>（收尾全部完成之后），不是这里。
/// </summary>
public interface IAppRestarter
{
    /// <summary>标记重启意图并开始正常关闭。幂等性由调用方保证不重复点击。</summary>
    void Restart();
}

/// <summary>
/// 重启意图的存放处。<see cref="App.OnExit"/> 在全部收尾完成后读它决定要不要拉起后继进程。
///
/// <para>
/// <b>为什么是独立单例而不是 VM 上的属性</b>：VM 会随窗口销毁，而 OnExit 读它的时刻
/// 窗口已经没了。重启意图是进程级状态，必须活在进程级对象上。
/// </para>
/// </summary>
public sealed class RestartRequest
{
    private int _requested;

    /// <summary>是否已请求重启（未被消费——OnExit 读它不改变状态，进程马上就退了）。</summary>
    public bool IsRequested => Volatile.Read(ref _requested) == 1;

    /// <summary>标记重启。幂等。</summary>
    public void Mark() => Interlocked.Exchange(ref _requested, 1);
}

/// <summary><see cref="IAppRestarter"/> 的标准实现：标记意图 + 触发正常关闭。</summary>
/// <param name="request">意图存放处（App.OnExit 读）</param>
/// <param name="shutdown">正常关闭动作（组合根注入 <c>Application.Current.Shutdown</c>；
/// 走主窗口 Closing → ShutdownCoordinator 全流程，资源收尾/看门狗信号一样不少）</param>
public sealed class AppRestarter(RestartRequest request, Action shutdown) : IAppRestarter
{
    /// <inheritdoc />
    public void Restart()
    {
        // 先标记、后关闭：顺序反了的话，万一关闭动作同步走完 OnExit，
        // OnExit 读到的还是"未请求"，后继进程就没人拉了。
        request.Mark();
        shutdown();
    }
}

/// <summary>
/// 拉起后继进程（F-69）。<b>只做一件事</b>：用与本进程相同的 exe + 原始命令行参数启动新实例。
/// 必须在 <c>OnExit</c> 的全部收尾（容器释放、端口释放、看门狗信号）之后调用——
/// 提前调的话，新旧实例会抢 9100 端口，新实例绑定失败就变成"重启完反而起不来"。
/// </summary>
public static class RestartLauncher
{
    /// <summary>
    /// 构造后继进程的启动信息。纯函数（不碰进程），测试钉参数重放：
    /// 重启必须带着原命令行（<c>--data-root</c> 等），否则数据根会悄悄回落。
    /// </summary>
    /// <param name="exePath">本程序 exe 的绝对路径（<c>Environment.ProcessPath</c>）</param>
    /// <param name="args">原始命令行参数（<b>不含</b> argv[0] 的 exe 路径）</param>
    public static ProcessStartInfo BuildStartInfo(string exePath, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exePath) { UseShellExecute = false };
        // ArgumentList 而不是手拼字符串：含空格的路径由框架按 Windows 规则加引号，
        // 手拼在以反斜杠结尾的路径上必然翻车（与看门狗参数重放同一教训，App.StartWatchdog）。
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        return psi;
    }

    /// <summary>
    /// 拉起后继进程。失败只返回 false（日志由调用方补）——重启失败时本进程已在退出路径上，
    /// 再抛异常只会把正常退出搞成崩溃退出，让看门狗重启出一个"没带重启意图"的实例。
    /// </summary>
    public static bool Launch(ProcessStartInfo psi)
    {
        try
        {
            return Process.Start(psi) is not null;
        }
        catch
        {
            // 启动失败（exe 被占用/权限/盘符变化）：放弃拉起。看门狗已收到正常退出信号不会补拉——
            // 这是可接受的降级：重启失败的最坏结果是"程序关了"，不是"程序坏了"。
            return false;
        }
    }
}
