using VisionStation.Devices.Vm;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// VM 链路状态判定（<see cref="VmLinkMonitor.Evaluate"/>）的防线用例。
///
/// 为什么这段值得单独测：它判错的代价不是"界面难看"，而是两头都会出事故 ——
///   · **该报的不报**：VM 崩了、流程停了，界面还显示正常 → 一整批漏检，直到有人发现良率不对；
///   · **不该报的乱报**：产线一停就刷告警 → 操作员学会无视它，真出事时也没人看。
/// 这两种都不靠"跑起来看一眼"能保证，只能把边界钉死。
/// </summary>
public class VmLinkMonitorTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void 未监听时_不管有没有客户端都是未监听()
    {
        var state = VmLinkMonitor.Evaluate(
            isListening: false,
            isClientConnected: true,
            lastActivityUtc: Now,
            nowUtc: Now,
            stallTimeout: Timeout);

        Assert.Equal(VmLinkState.NotListening, state);
    }

    [Fact]
    public void 监听中但客户端没连上_是NoClient()
    {
        var state = VmLinkMonitor.Evaluate(
            isListening: true,
            isClientConnected: false,
            lastActivityUtc: Now.AddMinutes(-5),   // 五分钟前有结果也不算数，连接已经断了
            nowUtc: Now,
            stallTimeout: Timeout);

        Assert.Equal(VmLinkState.NoClient, state);
    }

    [Fact]
    public void 重连后首帧到达前_以本次连接的时刻为基准_不判断流()
    {
        // 场景：VM 断开 5 分钟后重连，重连 3 秒还没有第一帧。
        // 基准必须是"这次连接建立了多久"，而不是上一轮连接的最后结果时刻 ——
        // 监听器在重连时会把 LastLineUtc 清零（见 VmResultListener.HandleClientAsync），
        // 调用方的 `LastLineUtc ?? ConnectedAtUtc` 这时才真的落到 ConnectedAtUtc 上。
        var reconnectAt = Now.AddSeconds(-3);

        var state = VmLinkMonitor.Evaluate(
            isListening: true,
            isClientConnected: true,
            lastActivityUtc: reconnectAt,
            nowUtc: Now,
            stallTimeout: Timeout);

        Assert.Equal(VmLinkState.Healthy, state);

        // 反证：同一条链路，如果把上一轮的时间戳（断开前 5 分钟）当基准，这一刻就是断流。
        // 这正是"重连时必须清 LastLineUtc"要消掉的那次误报（运行中会记一条 Error）。
        var withStaleTimestamp = VmLinkMonitor.Evaluate(
            isListening: true,
            isClientConnected: true,
            lastActivityUtc: Now.AddMinutes(-5),
            nowUtc: Now,
            stallTimeout: Timeout);

        Assert.Equal(VmLinkState.Stalled, withStaleTimestamp);
    }

    [Fact]
    public void 连着却拿不到任何时间基准_不能算健康()
    {
        // 连着 = VM 那头确实建立了 TCP 连接，但基准一个都拿不到（既没有结果时刻，
        // 也没有连接建立时刻）。这时"最近有结果"是无法证明的 ——
        // 返回 Healthy 等于替一条没有任何证据的链路担保"它刚推过"，界面会一直亮绿灯。
        // 四态里只有 Stalled（连着但没结果）是诚实的说法。
        var state = VmLinkMonitor.Evaluate(
            isListening: true,
            isClientConnected: true,
            lastActivityUtc: null,
            nowUtc: Now,
            stallTimeout: Timeout);

        Assert.Equal(VmLinkState.Stalled, state);
    }

    [Fact]
    public void 阈值内收到过结果_算正常()
    {
        var state = VmLinkMonitor.Evaluate(
            isListening: true,
            isClientConnected: true,
            lastActivityUtc: Now.AddSeconds(-9),
            nowUtc: Now,
            stallTimeout: Timeout);

        Assert.Equal(VmLinkState.Healthy, state);
    }

    [Fact]
    public void 超过阈值没有结果_是断流()
    {
        var state = VmLinkMonitor.Evaluate(
            isListening: true,
            isClientConnected: true,
            lastActivityUtc: Now.AddSeconds(-11),
            nowUtc: Now,
            stallTimeout: Timeout);

        Assert.Equal(VmLinkState.Stalled, state);
    }

    [Fact]
    public void 恰好等于阈值_还不算断流_下一毫秒才算()
    {
        // 边界刻意用 > 而不是 >=：节拍恰好卡在阈值上的产线不该每一件都抖一次告警。
        var atBoundary = VmLinkMonitor.Evaluate(
            isListening: true,
            isClientConnected: true,
            lastActivityUtc: Now.AddSeconds(-10),
            nowUtc: Now,
            stallTimeout: Timeout);

        var justOver = VmLinkMonitor.Evaluate(
            isListening: true,
            isClientConnected: true,
            lastActivityUtc: Now.AddMilliseconds(-10001),
            nowUtc: Now,
            stallTimeout: Timeout);

        Assert.Equal(VmLinkState.Healthy, atBoundary);
        Assert.Equal(VmLinkState.Stalled, justOver);
    }

    [Fact]
    public void 从未连上过且没在监听_是未监听而不是断流()
    {
        var state = VmLinkMonitor.Evaluate(
            isListening: false,
            isClientConnected: false,
            lastActivityUtc: null,
            nowUtc: Now,
            stallTimeout: Timeout);

        Assert.Equal(VmLinkState.NotListening, state);
    }
}
