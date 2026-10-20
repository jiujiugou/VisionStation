using VisionStation.Core.Models;

namespace VisionStation.Service.Recipes;

/// <summary>
/// 配方持久化端口。
///
/// 只负责保存：加载仍由组合根处理（含内嵌兜底），避免把兜底逻辑复制两份。
/// 调用方（ViewModel）只表达“保存这个配方”，不关心路径、JSON 格式和临时文件。
/// </summary>
public interface IRecipeStore
{
    /// <summary>
    /// 保存配方，返回最终写入的绝对路径。
    /// 失败必须抛异常，由调用方负责提示；不得静默回落到默认配方。
    /// </summary>
    Task<string> SaveAsync(RecipeConfig recipe, CancellationToken ct = default);
}
