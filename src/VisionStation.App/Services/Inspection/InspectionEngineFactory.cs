using VisionStation.Core.Engines;
using VisionStation.Core.Models;
using VisionStation.Engines.Vm;

namespace VisionStation.App.Services.Inspection;

/// <summary>
/// 按配方建引擎 —— 全系统**唯一**的"到底用哪套检测"决策点。
///
/// 现在只有一条路：<c>Vm</c>。ONNX/YOLO 推理已于 2026-10-27 整条删除
/// （引擎、离线跑批工具、模型、数据集、相关测试一起删），因为它没有生产调用者 ——
/// 留着两套检测路线，读代码的人无法判断哪条是主线，而产线只跑 VM 这一条。
///
/// 为什么还留着这个工厂、不直接在组合根里 <c>new VmExternalEngine()</c>：
///   "配方写错要立刻失败"这条判断需要一个落点。删掉工厂只是把这个 if 散回调用方，
///   换不来任何简化。
/// </summary>
internal static class InspectionEngineFactory
{
    /// <param name="recipe">已加载的配方</param>
    public static IInspectionEngine Create(RecipeConfig recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        if (string.Equals(recipe.Engine, EngineNames.Vm, StringComparison.OrdinalIgnoreCase))
        {
            // TCP 中转模式：检测在 VisionMaster 里跑，结果由 VM 主动推送过来（见 VmExternalEngine）。
            // 这里返回的是"接受推送"的占位实现，不是能主动取图检测的引擎 ——
            // 这个区别很关键，所以专门用一个类把它表达出来，不藏在 if 里。
            return new VmExternalEngine();
        }

        // 不回落、不静默换引擎：配方写了运行时没有的东西，启动就失败。
        // 静默换成 VM 会让"配了别的引擎却跑 VM 判据"这种事故排查一整天。
        throw new InvalidOperationException(
            $"配方「{recipe.Name}」指定引擎 \"{recipe.Engine}\"，但运行时只支持 \"{EngineNames.Vm}\"。"
            + "请把配方里的 engine 改成 Vm。");
    }
}
