namespace VisionStation.Devices.Vm;

/// <summary>
/// VM 结果链路的健康状态。
///
/// 为什么需要它：产线上四种完全不同的情况，在界面上原本是同一个黑屏 ——
///   ① 没配监听（启动日志里才有）；② VM 没连上来；③ 连着但流程没在跑；④ 一切正常但还没来料。
/// 前三种都要人去处理，第四种什么都别做。分不清就会出两种事故：
/// 要么把"没料"当故障（告警变背景噪音，真出事时没人看），
/// 要么把"VM 崩了"当没料（一整批漏检，直到有人发现良率不对）。
/// </summary>
public enum VmLinkState
{
    /// <summary>未启动监听：配置关了，或配方引擎不是 Vm</summary>
    NotListening,

    /// <summary>已监听，但没有客户端连着（VisionMaster 没启动，或地址/端口配错）</summary>
    NoClient,

    /// <summary>连着，且最近有结果（或在超时窗口内）</summary>
    Healthy,

    /// <summary>连着，但超过阈值没有结果 —— 流程没跑、触发没来，或推数据模块没被执行</summary>
    Stalled,
}

/// <summary>
/// 链路状态的判定规则。**纯函数，无 IO、无时钟依赖，可直接单测。**
///
/// 判定为什么不写在 <see cref="VmInspectionDriver"/> 里：那里有 Timer、有日志、有 Messenger，
/// 混进去就只能靠"跑起来看日志"验证。状态判定恰恰是最该用测试钉死的部分 ——
/// 它错了的代价是"该报的不报"。
/// </summary>
public static class VmLinkMonitor
{
    /// <summary>
    /// 算当前状态。
    /// </summary>
    /// <param name="isListening">监听器是否在跑</param>
    /// <param name="isClientConnected">是否有客户端连着</param>
    /// <param name="lastActivityUtc">
    /// 链路最后一次"有动静"的时刻：优先取最后一条结果的时刻，没有则取本轮连接建立的时刻。
    /// null = 从未连上过。
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

        // 连着、并且刚连上还没跑到第一件：以"连上多久"为基准，不能算断流。
        // 这条兜底是必须的 —— 否则每次 VM 重连后的头几秒都会误报一次。
        if (lastActivityUtc is null)
            return VmLinkState.Healthy;

        // 用 > 而不是 >=：恰好等于阈值的这一刻还不该翻脸，下一毫秒才翻。
        return nowUtc - lastActivityUtc.Value > stallTimeout
            ? VmLinkState.Stalled
            : VmLinkState.Healthy;
    }
}
