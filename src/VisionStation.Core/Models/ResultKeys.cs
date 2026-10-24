namespace VisionStation.Core.Models;

/// <summary>
/// 检测结果的约定键 —— 报告里"哪一项是什么"的唯一来源。
///
/// 为什么需要它：
///   换引擎之前，报告里键名是写死的 "Presence"/"Dimension"/"Defect"，
///   因为只有那三个工具。现在引擎可换、VM 的模块名由工程师在平台里随便起，
///   如果各处继续手写字符串，改一个模块名就会让数据库那三列悄悄变成 0，
///   而总判定还是对的 —— 这种"部分数据丢失但看起来正常"最难查。
///
/// 约定：
///   · 引擎负责把自家结果归到这三个角色上（VM 由配方里的 role 指定，ONNX 固定为 Defect）
///   · 存储层按这三个键投影到历史列；完整结果在 InspectionRecord.ToolResultsJson
/// </summary>
public static class ResultKeys
{
    /// <summary>定位/有无（零件在不在、位姿对不对）</summary>
    public const string Locate = "Locate";

    /// <summary>尺寸测量（直径、间距、角度）</summary>
    public const string Measure = "Measure";

    /// <summary>表面缺陷（划痕、麻点、夹杂）</summary>
    public const string Defect = "Defect";

    /// <summary>引擎级故障占用的键（引擎没跑起来时，报告里只有这一项）</summary>
    public const string Engine = "Engine";
}
