namespace VisionStation.Devices.Vm;

/// <summary>
/// VM 结果链路的健康状态。
///
/// 为什么需要它：产线上四种完全不同的情况，在界面上原本是同一个黑屏 ——
///   ① 没配监听（启动日志里才有）；② VM 没连上来；③ 连着但流程没在跑；④ 一切正常但还没来料。
/// 前三种都要人去处理，第四种什么都别做。分不清就会出两种事故：
/// 要么把"没料"当故障（告警变背景噪音，真出事时没人看），
/// 要么把"VM 崩了"当没料（一整批漏检，直到有人发现良率不对）。
///
/// <para>
/// <b>四态与处置</b>（状态本身不含处置动作，处置在 <c>VmInspectionDriver.ReportTransition</c>）：
/// <list type="table">
///   <item><term>NotListening</term><description>配置关了 / 配方引擎不是 Vm。改配置或换配方。</description></item>
///   <item><term>NoClient</term><description>端口在听，但 VM 没连上来。查 VM 的「发送数据」模块地址与端口、查防火墙。</description></item>
///   <item><term>Healthy</term><description>连着且最近有结果。什么都不做。</description></item>
///   <item><term>Stalled</term><description>连着但超过阈值没结果。VM 流程没跑 / 触发没来 / 发送模块没被执行。</description></item>
/// </list>
/// </para>
/// </summary>
public enum VmLinkState
{
    /// <summary>未启动监听：配置关了，或配方引擎不是 Vm</summary>
    NotListening,

    /// <summary>已监听，但没有客户端连着（VisionMaster 没启动，或地址/端口配错）</summary>
    NoClient,

    /// <summary>连着，且最近有结果（或在超时窗口内）</summary>
    Healthy,

    /// <summary>
    /// 连着，但超过阈值没有结果 —— 流程没跑、触发没来，或推数据模块没被执行。
    ///
    /// <para>
    /// 也用来兜"连着却拿不到任何时间基准"的情况（见 <see cref="VmLinkMonitor.Evaluate"/>）：
    /// 没有基准就证明不了"最近有结果"，四态里只有这一档是诚实的说法。
    /// </para>
    /// </summary>
    Stalled,
}

/// <summary>
/// 链路状态的判定规则。**纯函数，无 IO、无时钟依赖，可直接单测。**
///
/// 判定为什么不写在 <see cref="VmInspectionDriver"/> 里：那里有 Timer、有日志、有 Messenger，
/// 混进去就只能靠"跑起来看日志"验证。状态判定恰恰是最该用测试钉死的部分 ——
/// 它错了的代价是"该报的不报"。
///
/// <para>
/// <b>归属</b>：无状态静态类，没有实例、没有生命周期。唯一调用方是
/// <c>VmInspectionDriver.CheckLink</c>（1 秒一次的定时器回调），它负责把
/// "监听器在不在、有没有客户端、最后动静是什么时候"翻译成三个参数传进来。
/// </para>
///
/// <para>
/// <b>这里只判"是什么状态"，不判"该不该报警"</b>：日志级别（运行中 Error / 停机后 Info）
/// 在 App 层的 <c>ReportTransition</c> 里，判据是"工位是否在运行"（<c>TriggerStateMessage</c>）。
/// 分层的理由：Devices 层不知道什么叫"工位在运行"，那是业务状态；
/// 若把它塞进这里，这个纯函数就得依赖消息总线，也就没法单测了。
/// </para>
///
/// <para>
/// <b>阈值从哪来</b>：<c>VmLink:StallTimeoutSeconds</c>（默认 10 秒，启动期校验 1~3600）。
/// 代码里刻意不写默认值 —— 它是业务参数（由节拍与单件处理时间决定，经验取节拍的 3~5 倍），
/// 只能在现场定；写死一个数会让"这条产线到底该等多久"失去可查的依据。
/// </para>
/// </summary>
public static class VmLinkMonitor
{
    /// <summary>
    /// 算当前状态。判定顺序即优先级：没监听 → 没客户端 → （无基准 = 断流）→ 比阈值。
    /// </summary>
    /// <param name="isListening">监听器是否在跑</param>
    /// <param name="isClientConnected">是否有客户端连着</param>
    /// <param name="lastActivityUtc">
    /// 链路最后一次"有动静"的时刻：优先取最后一条结果的时刻，没有则取本轮连接建立的时刻。
    /// null = 连是连着，但一个基准都拿不到（"刚连上"的正常情况不会走到这里）。
    ///
    /// <para>
    /// 调用方传的是 <c>LastLineUtc ?? <see cref="VmResultListener.ConnectedAtUtc"/></c>：
    /// 重连时 <c>LastLineUtc</c> 会被清零（见 <see cref="VmResultListener"/> 里 HandleClientAsync 的说明），
    /// 于是这个兜底才真正生效 —— 重连后到首帧之间，基准是"这次连接建立了多久"，不是上一轮的时间戳。
    /// </para>
    /// </param>
    /// <param name="nowUtc">当前 UTC 时间（由调用方传入，便于测试固定时钟）</param>
    /// <param name="stallTimeout">多久没动静算断流</param>
    public static VmLinkState Evaluate(
        bool isListening,
        bool isClientConnected,
        DateTime? lastActivityUtc,
        DateTime nowUtc,
        TimeSpan stallTimeout)
    {
        if (!isListening)
            return VmLinkState.NotListening;

        if (!isClientConnected)
            return VmLinkState.NoClient;

        // 连着，但连"最后一次有动静"都不知道 —— **不能说健康**。
        //
        // 为什么不是 Healthy（这是本方法原来的写法）：Healthy 的语义是"最近有结果（或在超时窗口内）"，
        // 而 null 的意思是连计时的起点都没有。返回 Healthy 等于替 VM 担保"它刚推过"，
        // 而这正是最贵的一种谎：VM 明明一帧都没推，界面亮着绿灯，没人会去看。
        //
        // 什么时候会真的走到这里：正常路径不会 —— 调用方传 LastLineUtc ?? ConnectedAtUtc，
        // 而"连着"时 ConnectedAtUtc 正常都有值（连上时先写时刻、再让"已连接"可见，
        // 顺序见 VmResultListener.HandleClientAsync）。剩下的只有一处极端并发：
        // "最后一个客户端断开"与"新客户端刚连上"交错时，断开那侧清 0 可能抹掉新连接刚立下的基准。
        // 窗口只有几纳秒，但一旦落进去，旧写法会让绿灯一直亮到首帧到达 ——
        // 因为没有基准就永远算不出"超时"，一次都不会翻面。
        // 这就是把它改成 Stalled 的理由：**不知道就说不知道，宁可让人看一眼，
        // 也不要挂着绿灯替一条没有任何证据的链路背书。**
        //
        // 为什么不加第五态「未知」：四个状态是被界面消费的
        // （ImagePanelViewModel 按 State 分支改写空态文案），新增一态会让那个 switch 静默落空、
        // 把上一次的文案留在屏幕上 —— 要加就得连界面一起改，那一格的信息量不值这个改动面。
        // 四态里 Stalled 的措辞（"连着但没结果"）对这种情况恰好是诚实的。
        if (lastActivityUtc is null)
            return VmLinkState.Stalled;

        // 用 > 而不是 >=：恰好等于阈值的这一刻还不该翻脸，下一毫秒才翻。
        // 这条不是洁癖：定时器每秒跑一次，阈值 10 秒时会在第 10~11 秒之间某一次翻面；
        // 改成 >= 只是把翻面时刻提前最多 1 秒，但会让"刚好 10 秒"这种边界在单测里
        // 依赖于定时器的微小抖动 —— 用 > 时行为是可断言的。
        return nowUtc - lastActivityUtc.Value > stallTimeout
            ? VmLinkState.Stalled
            : VmLinkState.Healthy;
    }
}
