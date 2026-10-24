using OpenCvSharp;

namespace VisionStation.Core.Models;

/// <summary>
/// 一项检测的结论 —— 所有引擎的统一产出格式。
///
/// 一项 = 一个可独立判定的业务含义（外径、表面缺陷、有无孔…），
/// 不是"跑了一次算法"。VM 里一个模块可以出多项，ONNX 一次出缺陷一项，
/// 只要装进这个结构，界面、存库、追溯都不需要知道背后是平台还是自研。
///
/// 字段含义：
///   IsOk      —— 本项合不合格（判定依据由引擎给出：VM 用模块的 IsRunOK）
///   Summary   —— 一行中文摘要，直接给人看
///   Metrics   —— 可入报表的数值（如 Radius_px / DefectCount）
///   Overlays  —— 要画的标注。引擎只声明"画什么"，渲染层决定"怎么画"
/// </summary>
public class ToolResult
{
    /// <summary>本项检测是否合格（false = 不合格）</summary>
    public bool IsOk { get; set; }

    /// <summary>一行中文摘要，如 "外径: 12.68mm 偏差 0.02mm"</summary>
    public string Summary { get; set; } = "";

    /// <summary>
    /// 结果角色（<see cref="ResultKeys"/> 的 Locate / Measure / Defect，可空）。
    ///
    /// 为什么要它：报告里的键是业务名（"外径"、"表面缺陷"），而历史记录要按
    /// 定位/测量/缺陷三类投影到旧列。让业务名自由、让角色受约束，
    /// 两者互不干扰 —— 改一个中文标签不会让库里的列变成 0。
    /// </summary>
    public string Role { get; set; } = "";

    /// <summary>
    /// 测量指标字典（键名由引擎决定，配方里的 metric 字段可覆盖）。
    /// 例：{"Radius_px": 158.4} / {"DefectCount": 2, "MaxScore": 0.87}
    /// </summary>
    public Dictionary<string, double> Metrics { get; set; } = new();

    /// <summary>标注指令列表，由渲染层统一执行</summary>
    public List<DrawCommand> Overlays { get; set; } = new();

    /// <summary>快捷工厂：OK 结果</summary>
    public static ToolResult Ok(string summary, List<DrawCommand>? overlays = null)
        => new() { IsOk = true, Summary = summary, Overlays = overlays ?? new() };

    /// <summary>快捷工厂：NG 结果</summary>
    public static ToolResult Ng(string summary, List<DrawCommand>? overlays = null)
        => new() { IsOk = false, Summary = summary, Overlays = overlays ?? new() };
}

// ═══════════════════════════════════════════════════════
//  DrawCommand 体系 —— 引擎告诉渲染层"画什么"，不管"怎么画"
//  每种子类代表一种图形。加新图形类型只需加一个子类。
// ═══════════════════════════════════════════════════════

/// <summary>标注指令基类</summary>
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
