using OpenCvSharp;

namespace VisionStation.Core.Models;

/// <summary>
/// 一项检测的结论 —— 所有引擎的统一产出格式。
///
/// <para>
/// <b>定位</b>：它是 <see cref="InspectionReport.Results"/> 里的 Value，也是"这一项到底
/// 合不合格、测出来多少、要画在哪"的唯一载体。上游是 <c>VmResultParser</c>
/// （JSON 的 items 或分隔符的一段翻译成一个它），下游是
/// <c>InspectionRepository</c>（投影成旧列 + 序列化进 ToolResultsJson）
/// 与 <c>InspectionWorkflow.DrawAnnotations</c>（执行 Overlays）。
/// </para>
///
/// <para>
/// <b>归属</b>：每帧 new 若干个，跟着报告一起生灭；构造在收包线程，读在图处理线程池。
/// 没有共享实例，也没有跨帧复用 —— 复用会让"上一帧的标注"漏到这一帧的图上。
/// </para>
///
/// <para>
/// <b>失败行为</b>：纯数据对象，没有 IO、不抛异常。"这一项没问题"和"这一项没测"
/// 必须靠 <see cref="IsOk"/> + <see cref="Summary"/> 区分，不能靠 null ——
/// null 在字典里就是不存在的键，那会被当成"没这一项"而不是"这一项没过"。
/// </para>
///
/// <para>
/// 一项 = 一个可独立判定的业务含义（外径、表面缺陷、有无孔…），
/// 不是"跑了一次算法"。VM 里一个模块可以出多项，
/// 只要装进这个结构，界面、存库、追溯都不需要知道背后是平台还是自研。
/// </para>
///
/// <para>
/// <b>字段含义</b>：<see cref="IsOk"/> 判定依据由引擎给出（VM 用报文里的 ok，
/// 或整体跟随总判定）；<see cref="Summary"/> 是一行中文摘要，直接给人看；
/// <see cref="Metrics"/> 是可入报表的数值；<see cref="Overlays"/> 是"画什么"，
/// 渲染层决定"怎么画"。
/// </para>
/// </summary>
public class ToolResult
{
    /// <summary>
    /// 本项检测是否合格（false = 不合格）。
    ///
    /// 注意它是<b>可写属性而不是计算结果</b>：本程序不重算公差，
    /// 这个值是 VM 给的。想"顺手纠正一下 VM 的结论"是最危险的一类改动 ——
    /// 那会让配方与 .sol 出现两份判据，改一份忘另一份。
    /// </summary>
    public bool IsOk { get; set; }

    /// <summary>
    /// 一行中文摘要，如 "外径: 12.68mm 偏差 0.02mm"。
    /// 界面的明细列表、旧列投影、失败原因全用它 —— 所以它必须能<b>单独看懂</b>，
    /// 不能写成"见上"这类依赖上下文的话。
    /// </summary>
    public string Summary { get; set; } = "";

    /// <summary>
    /// 结果角色（<see cref="ResultKeys"/> 的 Locate / Measure / Defect，可空）。
    ///
    /// 为什么要它：报告里的键是业务名（"外径"、"表面缺陷"），而历史记录要按
    /// 定位/测量/缺陷三类投影到旧列。让业务名自由、让角色受约束，
    /// 两者互不干扰 —— 改一个中文标签不会让库里的列变成 0。
    ///
    /// 为空是合法状态：分隔符格式下角色来自配方 <c>vm.outputs[].role</c>，
    /// 没填就为空 —— 这时该项只存在于完整快照（ToolResultsJson，且快照里<b>不含 Role 字段</b>，
    /// 只有 IsOk / Summary / Metrics），三组旧列都会算"未执行"→ NG。
    /// 换句话说 Role 填错不会报错，只会让历史页那几列一直红着。
    /// </summary>
    public string Role { get; set; } = "";

    /// <summary>
    /// 测量指标字典（键名由引擎决定，配方里的 metric 字段可覆盖）。
    /// 例：{"Radius_px": 158.4} / {"DefectCount": 2, "MaxScore": 0.87}
    ///
    /// 存储层按固定键名取几个值填旧列（MatchScore / DesignedDiameter_mm …），
    /// 键名对不上就是 0 —— 所以配方里的 metric 不是随便起的名字，是列名。
    /// </summary>
    public Dictionary<string, double> Metrics { get; set; } = new();

    /// <summary>
    /// 标注指令列表，由渲染层统一执行。
    /// 引擎只说"这里有个框/圆/轮廓"，不碰画笔 —— 换引擎时渲染代码不用改。
    /// </summary>
    public List<DrawCommand> Overlays { get; set; } = new();

    /// <summary>快捷工厂：OK 结果。注意它不设 Role / Metrics，只有摘要 —— 调用方要补的自己补。</summary>
    public static ToolResult Ok(string summary, List<DrawCommand>? overlays = null)
        => new() { IsOk = true, Summary = summary, Overlays = overlays ?? new() };

    /// <summary>快捷工厂：NG 结果。失败占位、未执行占位都用它，摘要里要说清"为什么没过"。</summary>
    public static ToolResult Ng(string summary, List<DrawCommand>? overlays = null)
        => new() { IsOk = false, Summary = summary, Overlays = overlays ?? new() };
}

// ═══════════════════════════════════════════════════════
//  DrawCommand 体系 —— 引擎告诉渲染层"画什么"，不管"怎么画"
//  每种子类代表一种图形。加新图形类型只需加一个子类。
//
//  坐标系约定：**原图像素坐标**，不是上屏缩放后的坐标。
//  工作流先在全分辨率图上画，再把整张图缩到 1280 宽上屏 ——
//  顺序反过来（先缩再画）会让标注偏到零件外面，而且缩得越小偏得越多。
//  当前唯一的执行者是 InspectionWorkflow.DrawAnnotations。
//
//  现状（别高估这块）：运行时**没有任何代码构造这三种指令** ——
//  VM 报文体只带判定与数值，不带几何。所以上屏实际画出来的只有工作流
//  自己叠的 OK/NG 水印。这套类型留着，是因为它曾经是自研工具链的产物，
//  也是"引擎只声明画什么"这条设计的落点；接回带几何信息的引擎时不用重新设计。
// ═══════════════════════════════════════════════════════

/// <summary>标注指令基类。空基类是有意的：渲染层靠类型模式匹配分派，不需要共同字段。</summary>
public abstract class DrawCommand { }

/// <summary>画矩形框 —— 标注缺陷位置、定位区域</summary>
public class DrawRect : DrawCommand
{
    /// <summary>矩形区域（图像像素坐标）</summary>
    public Rect Rect { get; init; }

    /// <summary>颜色（BGR Scalar）</summary>
    public Scalar Color { get; init; }

    /// <summary>左上角标签文字（可选）</summary>
    public string? Label { get; init; }

    /// <summary>线宽（像素），默认 2</summary>
    public int Thickness { get; init; } = 2;
}

/// <summary>画圆 —— 标注尺寸测量找到的圆孔</summary>
public class DrawCircle : DrawCommand
{
    /// <summary>圆心（图像像素坐标）</summary>
    public Point Center { get; init; }

    /// <summary>半径（像素）</summary>
    public float Radius { get; init; }

    /// <summary>颜色（BGR Scalar）</summary>
    public Scalar Color { get; init; }

    /// <summary>标签文字（可选）</summary>
    public string? Label { get; init; }

    /// <summary>线宽（像素），默认 2</summary>
    public int Thickness { get; init; } = 2;
}

/// <summary>画轮廓 —— 标注不规则缺陷形状</summary>
public class DrawContour : DrawCommand
{
    /// <summary>轮廓点集（图像像素坐标）</summary>
    public Point[] Points { get; init; } = Array.Empty<Point>();

    /// <summary>颜色（BGR Scalar）</summary>
    public Scalar Color { get; init; }

    /// <summary>线宽（像素），默认 2</summary>
    public int Thickness { get; init; } = 2;
}
