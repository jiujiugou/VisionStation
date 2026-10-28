using VisionStation.App.Services.Interlock;
using VisionStation.Devices.Vm;

namespace VisionStation.App.Tests;

/// <summary>
/// F-47 启动联锁的真值表。
///
/// <para>
/// <see cref="StartInterlock.Evaluate"/> 是纯函数，六个条件的每种组合都能在这里穷举，
/// 不搭任何设备。最容易出的错是"漏判一种失败组合"，所以放行用例只有一个——
/// 其余任何一项不满足都必须拦。
/// </para>
/// </summary>
public class StartInterlockTests
{
    /// <summary>全就绪基线：链路健康、配方 Vm、无急停、不需要 PLC、自检全过。</summary>
    private static StartInterlockInput 全就绪(
        bool linkKnown = true,
        VmLinkState link = VmLinkState.Healthy,
        bool recipeVm = true,
        bool estop = false,
        bool plcRequired = false,
        bool plcOnline = false,
        bool healthRan = true,
        IReadOnlyList<string>? failedHealth = null)
        => new(linkKnown, link, recipeVm, estop, plcRequired, plcOnline, healthRan,
            failedHealth ?? Array.Empty<string>());

    [Fact]
    public void 全部就绪_放行()
    {
        // 放行用例只有这一个 —— 其余任何一项不满足都必须拦
        Assert.Empty(StartInterlock.Evaluate(全就绪()));
    }

    // ── 链路 ──

    [Fact]
    public void 链路状态未知_拦截()
    {
        var blocks = StartInterlock.Evaluate(全就绪(linkKnown: false));

        var reason = Assert.Single(blocks);
        Assert.Contains("未知", reason);
    }

    [Theory]
    [InlineData(VmLinkState.NotListening, "未监听")]
    [InlineData(VmLinkState.NoClient, "未连接")]
    [InlineData(VmLinkState.Stalled, "断流")]
    public void 链路异常态_拦截且原因随状态(VmLinkState state, string keyword)
    {
        var blocks = StartInterlock.Evaluate(全就绪(link: state));

        var reason = Assert.Single(blocks);
        Assert.Contains(keyword, reason);
    }

    // ── 配方 / 急停 ──

    [Fact]
    public void 配方引擎声明非Vm_拦截()
    {
        var blocks = StartInterlock.Evaluate(全就绪(recipeVm: false));

        var reason = Assert.Single(blocks);
        Assert.Contains("Vm", reason);
    }

    [Fact]
    public void 急停未复位_拦截()
    {
        var blocks = StartInterlock.Evaluate(全就绪(estop: true));

        var reason = Assert.Single(blocks);
        Assert.Contains("复位", reason);
    }

    // ── PLC（只在 PLC 触发时才相关）──

    [Fact]
    public void PLC触发且离线_拦截()
    {
        var blocks = StartInterlock.Evaluate(全就绪(plcRequired: true, plcOnline: false));

        var reason = Assert.Single(blocks);
        Assert.Contains("PLC", reason);
    }

    [Fact]
    public void 非PLC触发_离线不拦_手动定时与PLC无关()
    {
        Assert.Empty(StartInterlock.Evaluate(全就绪(plcRequired: false, plcOnline: false)));
    }

    // ── 自检 ──

    [Fact]
    public void 自检没跑_拦截()
    {
        var blocks = StartInterlock.Evaluate(全就绪(healthRan: false));

        var reason = Assert.Single(blocks);
        Assert.Contains("自检", reason);
    }

    [Fact]
    public void 自检有失败项_拦截且点名()
    {
        string[] failed = ["数据库", "结果推送入口"];
        var blocks = StartInterlock.Evaluate(全就绪(failedHealth: failed));

        var reason = Assert.Single(blocks);
        Assert.Contains("数据库", reason);
        Assert.Contains("结果推送入口", reason);
    }

    [Fact]
    public void 多条件同时失败_逐条列出_不合并()
    {
        var blocks = StartInterlock.Evaluate(全就绪(linkKnown: false, estop: true, healthRan: false));

        // 每个失败条件一条原因：操作员要的是清单，不是一句"未就绪"
        Assert.Equal(3, blocks.Count);
    }
}
