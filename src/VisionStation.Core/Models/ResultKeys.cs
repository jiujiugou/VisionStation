namespace VisionStation.Core.Models;

/// <summary>
/// 检测结果的约定键 —— 报告里"哪一项是什么"的唯一来源。
///
/// <para>
/// <b>定位</b>：这是一个纯常量类，但它是三处约定的交集：
/// <list type="bullet">
///   <item><c>InspectionRepository.SaveAsync</c> 按前三个键做旧列投影
///         （定位/测量/缺陷三组列），键名对不上那三列就恒为"未执行"→ NG；</item>
///   <item><c>InspectionReport.Failure</c> 用 <see cref="Engine"/> 这个键装失败原因；</item>
///   <item><c>InspectionWorkflow</c> 入库后按 <see cref="Engine"/> 键判断"这一帧是引擎故障还是判定"，
///         并把它当 ERROR 记日志。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>为什么需要它</b>：换引擎之前，报告里键名是写死的 "Presence"/"Dimension"/"Defect"，
/// 因为只有那三个工具。现在引擎可换、VM 的模块名由工程师在平台里随便起，
/// 如果各处继续手写字符串，改一个模块名就会让数据库那三列悄悄变成 0，
/// 而总判定还是对的 —— 这种"部分数据丢失但看起来正常"最难查。
/// </para>
///
/// <para>
/// <b>约定</b>：
/// <list type="bullet">
///   <item>引擎负责把自家结果归到这三个角色上（VM 由配方里的 <c>role</c> 指定）；</item>
///   <item>存储层按这三个键投影到历史列；完整结果在 <c>InspectionRecord.ToolResultsJson</c>；</item>
///   <item><b>配方里的 label 不要取这四个名字</b> —— 尤其是 "Engine"：
///         那一项会被 <c>InspectionWorkflow</c> 当成引擎故障记 Error（误报），
///         而失败报告写进来时也会把同名项覆盖掉。</item>
/// </list>
/// </para>
/// </summary>
public static class ResultKeys
{
    /// <summary>
    /// 定位/有无（零件在不在、位姿对不对）。
    /// 对应历史记录里的 Presence* 三列；配方里没给这个角色时，那三列是"未执行"（NG）。
    /// </summary>
    public const string Locate = "Locate";

    /// <summary>
    /// 尺寸测量（直径、间距、角度）。
    /// 对应 Dimension* 与 DesignDiameter / MeasuredDiameter / Deviation 三列，
    /// 后三者要从 Metrics 里按约定键名取值，键名不对就是 0（不是 null，看不出是缺数据）。
    /// </summary>
    public const string Measure = "Measure";

    /// <summary>
    /// 表面缺陷（划痕、麻点、夹杂）。对应 Defect* 与 ScratchCount / NotchCount 两列。
    /// </summary>
    public const string Defect = "Defect";

    /// <summary>
    /// 引擎级故障占用的键（引擎没跑起来 / 报文解析失败时，报告里只有这一项）。
    ///
    /// 它<b>不是</b>一个业务角色：<c>Role</c> 留空，所以不会污染三组旧列，
    /// 但会让总判定变 NG —— 这正是要的："这一件没判出来"必须体现在良率里，
    /// 否则会变成"没记录 = 没发生"。
    /// </summary>
    public const string Engine = "Engine";
}
