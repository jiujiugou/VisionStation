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
    public void 刚连上还没有任何活动_算正常_不误报断流()
    {
        // 这条是重连场景的护栏：VM 每次重连后的头几秒都没有"最后结果时间"，
        // 如果把 null 当成"很久以前"，每次重连都会闪一次假告警。
        var state = VmLinkMonitor.Evaluate(
            isListening: true,
            isClientConnected: true,
            lastActivityUtc: null,
            nowUtc: Now,
            stallTimeout: Timeout);

        Assert.Equal(VmLinkState.Healthy, state);
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
