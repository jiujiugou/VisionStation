namespace VisionStation.Devices;

/// <summary>
/// 一次触发的来源。
///
/// 为什么需要它：报告里的 <c>TriggerSource</c> 字段一直存在，但自动模式写死了 "Manual" ——
/// 定时触发的记录也记成手动，于是"这批 NG 是定时拍的还是人点出来的"这个问题
/// 在历史页上永远查不出来。字段有、语义没做到，比没有字段更误导。
/// </summary>
public enum TriggerOrigin
{
    /// <summary>人按的：单次触发按钮、启动时的第一次</summary>
    Manual,

    /// <summary>定时器到点（自动模式）</summary>
    Timer,

    /// <summary>PLC 给的上升沿</summary>
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
    /// <summary>写进 <c>InspectionReport.TriggerSource</c> 的值</summary>
    public static string ToReportValue(this TriggerOrigin origin) => origin switch
    {
        TriggerOrigin.Manual => "Manual",
        TriggerOrigin.Timer => "Timer",
        TriggerOrigin.Plc => "PLC",
        _ => "Manual",
    };
}
