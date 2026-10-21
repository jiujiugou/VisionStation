using VisionStation.Core.Models;

namespace VisionStation.Service.Recipes;

/// <summary>
/// 配方列表项 —— 只带"选配方时要看的"信息，不加载完整配方。
///
/// 为什么单独一个 record：配方页要列 10 份配方时，没必要把每份的 ROI、工具、参数全反序列化一遍。
/// </summary>
/// <param name="Name">配方自报的名字（可能和文件名不同，比如中文名）</param>
/// <param name="FileName">磁盘上的文件名（含 .json），切换时用它做标识</param>
/// <param name="Width">配方声明的画面宽，0 = 未声明</param>
/// <param name="Height">配方声明的画面高，0 = 未声明</param>
/// <param name="UpdatedAt">最近保存时间</param>
/// <param name="ToolCount">启用的工具数量（让人一眼看出这份配方跑几个工具）</param>
/// <param name="IsActive">是否是当前激活的那份</param>
public sealed record RecipeSummary(
    string Name,
    string FileName,
    int Width,
    int Height,
    DateTimeOffset? UpdatedAt,
    int ToolCount,
    bool IsActive);

/// <summary>
/// 配方持久化端口。
///
/// 从"单文件保存"扩展为"配方目录"（S7）：工业现场换产品靠**切换配方**，不是逐个改参数，
/// 所以存储层必须能列出多份、加载指定一份、记住当前用哪一份。
///
/// 仍然只负责持久化：加载时的"坏文件兜底"由组合根处理，避免兜底逻辑复制两份。
/// </summary>
public interface IRecipeStore
{
    /// <summary>配方目录（同一目录下的多个 json 就是多份配方）</summary>
    string Folder { get; }

    /// <summary>当前激活的配方文件名；目录里一份配方都没有时为 null</summary>
    string? ActiveName { get; }

    /// <summary>
    /// 保存配方，返回最终写入的绝对路径。
    /// 目标文件 = 当前激活的那份（没有激活项时用 default.json）。
    /// 会先把旧文件备份到 .backup 子目录再原子替换 —— 现场改坏了要能退回去。
    /// 失败必须抛异常，由调用方负责提示；不得静默回落到默认配方。
    /// </summary>
    Task<string> SaveAsync(RecipeConfig recipe, CancellationToken ct = default);

    /// <summary>列出目录下所有能解析的配方。解析失败的文件跳过（记日志），不抛异常。</summary>
    Task<IReadOnlyList<RecipeSummary>> ListAsync(CancellationToken ct = default);

    /// <summary>按文件名加载一份配方。文件不存在或解析失败返回 null。</summary>
    Task<RecipeConfig?> LoadAsync(string fileName, CancellationToken ct = default);

    /// <summary>把某份配方设为当前配方（写 .active 指针）。文件不存在时抛异常。</summary>
    Task SetActiveAsync(string fileName, CancellationToken ct = default);
}
