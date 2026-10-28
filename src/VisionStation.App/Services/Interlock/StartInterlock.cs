using VisionStation.Devices;
using VisionStation.Devices.Vm;

namespace VisionStation.App.Services.Interlock;

/// <summary>
/// 启动联锁的输入 —— 只收原始数据，不收服务对象。
///
/// <para>
/// 为什么是平铺的 bool/枚举而不是一串接口：<see cref="StartInterlock.Evaluate"/> 是纯函数，
/// 判定逻辑要能在测试里不搭任何设备的情况下穷举；"这些数据从哪来"是
/// <c>ControlPanelViewModel</c> 的事（链路状态来自消息、PLC 在线来自活查、自检来自快照）。
/// </para>
/// </summary>
/// <param name="LinkStatusKnown">
/// 是否收到过 <c>VmLinkStatusMessage</c>。驱动只在状态翻面时广播 —— 一条都没收到
/// 等于监听根本没起来，联锁必须把"未知"当失败，不能当通过。
/// </param>
/// <param name="LinkState">最近一次链路状态（<paramref name="LinkStatusKnown"/> 为 false 时无意义）</param>
/// <param name="RecipeEngineIsVm">配方引擎声明是否为 Vm（运行时唯一可用的引擎）</param>
/// <param name="EStopLatched">急停已触发且未复位</param>
/// <param name="PlcRequired">触发源是否为 PLC（手动/定时触发不需要 PLC 在线）</param>
/// <param name="PlcOnline">PLC 当前是否在线（<paramref name="PlcRequired"/> 为 false 时无意义）</param>
/// <param name="HealthCheckRan">启动自检是否已运行</param>
/// <param name="FailedHealthItems">自检失败项名称，空列表 = 全过</param>
public sealed record StartInterlockInput(
    bool LinkStatusKnown,
    VmLinkState LinkState,
    bool RecipeEngineIsVm,
    bool EStopLatched,
    bool PlcRequired,
    bool PlcOnline,
    bool HealthCheckRan,
    IReadOnlyList<string> FailedHealthItems);

/// <summary>
/// F-47 启动联锁 —— 进入自动运行前检查产线条件，不齐就不放行。
///
/// <para>
/// <b>为什么是"真拦"而不是"记警告照样跑"</b>：带病启动不会炸，会<b>静默跑出一批废数据</b> ——
/// VM 没连上就开始跑，触发来了没有结果；急停没复位就继续，操作员以为已经恢复安全。
/// 等发现时已经一整批了。这比崩溃难查得多，所以拦截是唯一有意义的形态。
/// </para>
///
/// <para>
/// <b>拦的是谁</b>：「进入自动运行」（<c>ToggleAutoMode</c>）。急停和停止<b>永不拦</b> ——
/// 安全动作必须任何时候都能按下去，这是 F-42 拍板时就定下的底线。
/// </para>
///
/// <para>
/// <b>为什么是纯函数</b>：六个条件的组合就是一张真值表，最容易出"漏判一种失败组合"的错；
/// 纯函数让每个条件、每种组合都能在测试里穷举，不搭设备。
/// </para>
/// </summary>
public static class StartInterlock
{
    /// <summary>
    /// 逐项检查，返回<b>失败原因列表</b>（人话，直接可进日志面板）；空列表 = 放行。
    /// 每个失败条件一条原因，不合并 —— 操作员要的是"哪几件事没就绪"，不是一句"未就绪"。
    /// </summary>
    public static IReadOnlyList<string> Evaluate(StartInterlockInput input)
    {
        var blocks = new List<string>();

        // ── 1. VM 结果链路 ──
        // 没收到过任何链路状态 = 监听没起来（配置关了 / 引擎不是 Vm / 启动顺序异常）。
        // "未知"按失败算：放行一个可能收不到结果的自动运行，等于默许跑废数据。
        if (!input.LinkStatusKnown)
            blocks.Add("VM 链路状态未知：结果监听可能未启动");
        else
        {
            switch (input.LinkState)
            {
                case VmLinkState.NotListening:
                    blocks.Add("VM 结果链路未监听：检查 VmLink 配置与配方引擎声明");
                    break;
                case VmLinkState.NoClient:
                    blocks.Add("VisionMaster 未连接：结果端口上没有客户端");
                    break;
                case VmLinkState.Stalled:
                    blocks.Add("VM 链路断流：连接还在但超过阈值没有收到结果");
                    break;
                case VmLinkState.Healthy:
                    break;
            }
        }

        // ── 2. 配方引擎声明 ──
        // 运行时引擎是启动期按配方选的，走到这里配方一定已加载（非法配方根本起不来）；
        // 这里拦的是"引擎声明不是 Vm"——当前只有 VM 一个引擎，别的声明值没有可执行的东西。
        if (!input.RecipeEngineIsVm)
            blocks.Add("配方引擎声明不是 Vm：当前只支持 VisionMaster 推送模式");

        // ── 3. 急停未复位 ──
        // 急停按下后必须人工复位才能重新进入自动运行 —— 这是联锁唯一"卡住不放"的条件，
        // 复位动作在控制面板上（会写 EStopReset 审计）。
        if (input.EStopLatched)
            blocks.Add("急停已触发且未复位：先在控制面板复位急停");

        // ── 4. PLC 在线（仅 PLC 触发时） ──
        // 手动/定时触发不需要 PLC；PLC 触发离线 = 触发来了收不到，自动运行就是空转。
        if (input.PlcRequired && !input.PlcOnline)
            blocks.Add("PLC 离线：触发依赖 PLC 信号，离线状态下自动运行收不到触发");

        // ── 5. 启动自检 ──
        // 自检是启动期跑一次的（F-49），这里看的是它的结果快照，不是重跑。
        if (!input.HealthCheckRan)
            blocks.Add("启动自检尚未运行：无法确认存储与结果推送入口就绪");
        else if (input.FailedHealthItems.Count > 0)
            blocks.Add($"启动自检未通过：{string.Join("、", input.FailedHealthItems)}");

        return blocks;
    }
}
