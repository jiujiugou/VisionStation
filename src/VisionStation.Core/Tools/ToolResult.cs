using OpenCvSharp;

namespace VisionStation.Core.Tools;

/// <summary>
/// 工具执行结果 —— 所有 IVisionTool 的统一产出格式
///
/// 为什么要统一？
///   之前每个 Detector 各自返回 DetectionResult（字段名不一样、标注逻辑嵌在各个 Detector 里）。
///   统一为 ToolResult 后：
///     1. Pipeline 只关心 ToolResult（不关心哪个工具返回的）
///     2. UI 层只读 ToolResult 的 Summary/Metrics
///     3. 渲染层遍历 DrawCommands 统一画标注（Detector 只负责"画什么"，不管"怎么画"）
///
/// 使用：
///   var result = tool.Execute(gray, roi);
///   result.IsOk         → 合不合格
///   result.Summary      → 一行中文摘要
///   result.Metrics      → 具体指标（如 { MatchScore: 0.934, Deviation_mm: 0.05 }）
///   result.Overlays     → DrawCommand 列表（渲染层遍历画标注）
/// </summary>
public class ToolResult
{
    /// <summary>本项检测是否合格（false = 不合格）</summary>
    public bool IsOk { get; set; }

    /// <summary>一行中文摘要，如 "设计:10.00mm 实测:10.05mm 偏差:0.05mm"</summary>
    public string Summary { get; set; } = "";

    /// <summary>
    /// 测量指标字典。
    /// 如 PresenceTool 返回 {"MatchScore": 0.934}
    ///    DimensionTool 返回 {"MeasuredDiameter_mm": 10.05, "Deviation_mm": 0.05}
    ///    DefectTool 返回 {"ScratchCount": 2, "NotchCount": 0}
    /// </summary>
    public Dictionary<string, double> Metrics { get; set; } = new();

    /// <summary>
    /// 标注指令列表。
    /// 工具只管声明"我要画什么"（DrawRect / DrawCircle / DrawContour），
    /// 渲染层（InspectionWorkflow 的标注段）统一执行绘制。
    /// </summary>
    public List<DrawCommand> Overlays { get; set; } = new();

    /// <summary>快捷工厂：OK 结果</summary>
    public static ToolResult Ok(string summary, List<DrawCommand>? overlays = null)
        => new() { IsOk = true, Summary = summary, Overlays = overlays ?? new() };

    /// <summary>快捷工厂：NG 结果</summary>
    public static ToolResult Ng(string summary, List<DrawCommand>? overlays = null)
        => new() { IsOk = false, Summary = summary, Overlays = overlays ?? new() };
}

// ═══════════════════════════════════════════════════════
//  DrawCommand 体系 —— 工具告诉渲染层"画什么"，不管"怎么画"
//  每种子类代表一种图形。加新图形类型只需加一个子类。
// ═══════════════════════════════════════════════════════

/// <summary>标注指令基类</summary>
public abstract class DrawCommand { }

/// <summary>画矩形框 —— 用于标注 ROI 位置、匹配结果区域</summary>
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

/// <summary>画圆 —— 用于标注尺寸检测找到的圆孔</summary>
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

/// <summary>画轮廓 —— 用于标注缺陷形状（不规则 Blob）</summary>
public class DrawContour : DrawCommand
{
    /// <summary>轮廓点集（图像像素坐标）</summary>
    public Point[] Points { get; init; } = Array.Empty<Point>();

    /// <summary>颜色（BGR Scalar）</summary>
    public Scalar Color { get; init; }

    /// <summary>线宽（像素），默认 2</summary>
    public int Thickness { get; init; } = 2;
}
