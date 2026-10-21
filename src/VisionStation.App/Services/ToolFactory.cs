using System.IO;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;

namespace VisionStation.App.Services;

/// <summary>
/// 工具工厂 —— 从配方配置创建 IVisionTool 列表
///
/// 职责：Type 字符串 → Tool 实例的映射。
/// 加新工具只改这里，App.xaml.cs 不感知。
/// </summary>
public class ToolFactory
{
    private readonly Calibration _calibration;
    private readonly RecipeConfig _recipe;
    private readonly string? _templatePath;

    public ToolFactory(Calibration calibration, RecipeConfig recipe, string? templatePath = null)
    {
        _calibration = calibration;
        _recipe = recipe;
        _templatePath = templatePath;
    }

    /// <summary>
    /// 从配方创建工具列表。只创建 Enabled = true 的。
    /// </summary>
    public List<IVisionTool> CreateAll()
    {
        var tools = new List<IVisionTool>();

        foreach (var tc in _recipe.Tools)
        {
            if (!tc.Enabled) continue;

            var tool = CreateOne(tc);
            if (tool != null)
                tools.Add(tool);
        }

        return tools;
    }

    /// <summary>
    /// Type 字符串 → Tool 实例
    /// 加新工具：在这个 switch 里加一个 case，App.xaml.cs 一行不改。
    /// </summary>
    private IVisionTool? CreateOne(ToolConfigData tc)
    {
        switch (tc.Type)
        {
            case "Presence":
                var pt = new PresenceTool(tc.Params.GetValueOrDefault("threshold", 0.70));
                var templatePath = ResolveTemplatePath(tc);
                if (templatePath != null && File.Exists(templatePath))
                    pt.LoadTemplate(templatePath);
                return pt;

            case "Dimension":
                return new DimensionTool(_calibration,
                    _recipe.Calibration.DesignedDiameterMm,
                    _recipe.Calibration.DimensionToleranceMm);

            case "Defect":
                var dt = new DefectTool(
                    tc.Params.GetValueOrDefault("areaMin", 50),
                    tc.Params.GetValueOrDefault("thresholdValue", 128));
                // 孔排除圆（图像坐标）：零件固有孔（如垫圈中心孔）不判缺陷
                dt.ExcludeCenterX = tc.Params.GetValueOrDefault("excludeX", -1);
                dt.ExcludeCenterY = tc.Params.GetValueOrDefault("excludeY", -1);
                dt.ExcludeRadius = tc.Params.GetValueOrDefault("excludeRadius", 0);
                return dt;

            case "Yolo":
                // 深度学习缺陷检测：模型缺失时降级为"不注册该工具"，
                // 不能让一个可选模型把整条产线检测卡死
                var modelPath = ResolveModelPath(tc.Model);
                if (modelPath == null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[ToolFactory] 未找到 YOLO 模型（models/neu-det.onnx），跳过 Yolo 工具");
                    return null;
                }
                return new OnnxYoloTool(
                    modelPath,
                    (float)tc.Params.GetValueOrDefault("conf", 0.25),
                    (float)tc.Params.GetValueOrDefault("nms", 0.45))
                {
                    // 工具名必须与 ROI 名一致，否则 Pipeline 会走索引兜底匹配，框会画到别的区域
                    Name = string.IsNullOrWhiteSpace(tc.Roi) ? "Yolo" : tc.Roi
                };

            // 将来：
            // case "OCR": return new OcrTool(...)
            // case "Barcode": return new BarcodeTool(...)

            default:
                System.Diagnostics.Debug.WriteLine($"[ToolFactory] 未知工具类型: {tc.Type}");
                return null;
        }
    }

    /// <summary>
    /// 解析 Presence 模板路径（S7：模板可以随配方走）。
    ///
    /// 顺序：
    ///   1. 工具自己的 template 字段（换分辨率 = 换模板，两份配方各带各的）
    ///   2. 全局默认 Detection:TemplatePath（单配方时代的写法，保持兼容）
    ///
    /// 相对路径按 exe 目录解析，找不到再交回调用方按 cwd 处理 —— 与 AppPaths 的读资源策略一致。
    /// </summary>
    private string? ResolveTemplatePath(ToolConfigData tc)
    {
        var value = string.IsNullOrWhiteSpace(tc.Template) ? _templatePath : tc.Template;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Path.IsPathRooted(value)) return value;

        var basePath = Path.Combine(AppContext.BaseDirectory, value);
        return File.Exists(basePath) ? basePath : Path.GetFullPath(value);
    }

    /// <summary>
    /// 解析 YOLO 模型路径。
    ///
    /// 顺序：
    ///   1. 配方里显式写的 model 字段（绝对路径，现场部署时用）
    ///   2. 输出目录下的 models/neu-det.onnx（发布版）
    ///   3. 从输出目录逐级向上找 models/neu-det.onnx（开发时 bin/Debug/net8.0-windows 往上 4 层）
    ///
    /// 为什么不用相对路径？
    ///   WPF 的工作目录在双击启动和 VS 调试时不一样，
    ///   相对路径会时灵时不灵，逐级向上找是唯一稳定的写法。
    /// </summary>
    private static string? ResolveModelPath(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "models", "neu-det.onnx");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
