namespace VisionStation.Devices;

/// <summary>
/// 一次触发的来源。
///
/// 为什么需要它：报告里的 <c>TriggerSource</c> 字段一直存在，但自动模式写死了 "Manual" ——
/// 定时触发的记录也记成手动，于是"这批 NG 是定时拍的还是人点出来的"这个问题
/// 在历史页上永远查不出来。字段有、语义没做到，比没有字段更误导。
///
/// <para>
/// <b>为什么不能一律写 Manual</b>：这一列是排查的第一步分叉 ——
/// 定时/PLC 触发的 NG 通常是节拍或来料问题（该调工艺），人点出来的 NG 往往是试机或复判（不用管）；
/// PLC 触发还额外意味着"上一道工序认为这件该检"。三者混成一个值，
/// 历史页上的"触发来源"就退化成常量，等于删了这一列，但界面还假装它在。
/// </para>
///
/// <para>
/// <b>注意本枚举不是取值的全集</b>：VM 推送的帧不走这里，它的 <c>TriggerSource</c>
/// 由 <c>VmResultParser</c> 写 —— 报文里带什么就存什么，没带才兜底成 "VM"。
/// 也就是说库里除了 Manual / Timer / PLC，还会出现 "VM" 甚至 VM 侧自定义的字符串。
/// 做历史筛选时按字符串匹配，别只按本枚举反推。
/// </para>
/// </summary>
public enum TriggerOrigin
{
    /// <summary>
    /// 人按的：单次触发按钮、启动时的第一次。
    /// 也是枚举里唯一一个"不代表外部节拍"的值 —— 它意味着这一帧的时机由人决定。
    /// </summary>
    Manual,

    /// <summary>定时器到点（自动模式）。用于"无 PLC 的演示/试跑"场景。</summary>
    Timer,

    /// <summary>PLC 给的上升沿。产线正常运行时应当只有这一种。</summary>
    Plc,
}

/// <summary>
/// 触发来源到"报告里那个字符串"的映射。
///
/// 只在这一处把枚举翻成字符串：存库、界面、历史筛选都读同一个值。
/// 分散写的话，迟早出现库里是 "Timer"、界面上写 "定时"、筛选时又按 "Timer" 匹配不上。
/// </summary>
public static class TriggerOriginExtensions
{
    /// <summary>
    /// 写进 <c>InspectionReport.TriggerSource</c> 的值。
    ///
    /// <para>
    /// 末行的 <c>_ =&gt; "Manual"</c> 是**兜底而不是等价**：新增枚举值却忘了在这里加分支时，
    /// 记录会被静默记成"人按的"，而不是报错或写个"Unknown"。
    /// 这是有意选的（宁可值不精确也不要空字符串进库），代价是**加枚举必须同时改这里**。
    /// </para>
    /// </summary>
    public static string ToReportValue(this TriggerOrigin origin) => origin switch
    {
        TriggerOrigin.Manual => "Manual",
        TriggerOrigin.Timer => "Timer",
        TriggerOrigin.Plc => "PLC",
        _ => "Manual",
    };
}
