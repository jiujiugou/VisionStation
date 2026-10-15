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
                if (!string.IsNullOrEmpty(_templatePath) && File.Exists(_templatePath))
                    pt.LoadTemplate(_templatePath);
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

            // 将来：
            // case "OCR": return new OcrTool(...)
            // case "Barcode": return new BarcodeTool(...)

            default:
                System.Diagnostics.Debug.WriteLine($"[ToolFactory] 未知工具类型: {tc.Type}");
                return null;
        }
    }
}
