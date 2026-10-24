using Microsoft.Extensions.Logging;
using VisionStation.Core.Engines;
using VisionStation.Core.Models;
using VisionStation.Engines.Onnx;
using VisionStation.Engines.Vm;

namespace VisionStation.App.Services.Inspection;

/// <summary>
/// 按配方选引擎 —— 全系统**唯一**的"到底用哪套检测"决策点。
///
/// 为什么集中在这一个方法里：
///   引擎选择是组合根的事。散到 Workflow / VM / 自检里各自判断一次，
///   就会出现"Workflow 用 VM、自检查 ONNX 模型"这种自相矛盾的状态，
///   而且没人能回答"现在到底用的是什么"。
///
/// 资源路径在这里一次性解析成绝对路径：配方里写相对路径（随 exe 部署），
/// 引擎拿到的一定是绝对路径 —— 引擎不知道也不关心部署目录怎么定。
/// </summary>
internal static class InspectionEngineFactory
{
    /// <param name="recipe">已加载的配方</param>
    /// <param name="assetRoot">资源基准目录（通常是 exe 目录）</param>
    /// <param name="loggerFactory">日志工厂（可空）</param>
    public static IInspectionEngine Create(
        RecipeConfig recipe, string assetRoot, ILoggerFactory? loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        if (string.Equals(recipe.Engine, EngineNames.Vm, StringComparison.OrdinalIgnoreCase))
        {
            // TCP 中转模式：检测在 VisionMaster 里跑，结果由 VM 主动推送过来（见 VmExternalEngine）。
            // 这里返回的是"接受推送"的占位实现，不是能主动取图检测的引擎 ——
            // 这个区别很关键，所以专门用一个类把它表达出来，不藏在 if 里。
            return new VmExternalEngine();
        }

        if (string.Equals(recipe.Engine, EngineNames.Onnx, StringComparison.OrdinalIgnoreCase))
        {
            var model = RecipeConfig.ResolveAsset(recipe.Onnx.ModelPath, assetRoot);
            return new OnnxInspectionEngine(model, recipe.Onnx,
                loggerFactory?.CreateLogger<OnnxInspectionEngine>());
        }

        // 走到这里说明配方写错了。**不回落默认引擎** ——
        // 静默换成 ONNX 会让"配了 VM 却在跑 YOLO"这种事故排查一整天，
        // 启动期直接失败反而是最省事的结果。
        throw new InvalidOperationException(
            $"配方「{recipe.Name}」指定了未知引擎 \"{recipe.Engine}\"，"
            + $"可选值：{string.Join(" / ", EngineNames.All)}");
    }
}
