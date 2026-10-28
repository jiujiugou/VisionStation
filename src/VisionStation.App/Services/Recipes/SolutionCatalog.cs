using System.IO;
using VisionStation.Core.Models;
using VisionStation.Service.Recipes;

namespace VisionStation.App.Services.Recipes;

/// <summary>
/// 方案目录（<c>Config\solutions</c>）的门面 —— 配方页「从方案导入」（F-67）与
/// 切换契约闸（F-68）对 .sol 的唯一访问口。
///
/// <para>
/// <b>为什么 VM 不直接调 <see cref="SolutionManifestReader"/></b>：解析需要把配方里的
/// 相对 <c>solutionPath</c> 用 assetRoot 解析成绝对路径——那是组合根才知道的事。
/// 把"目录扫描 + 路径解析 + 名册读取 + 契约校验"收在这里，VM 只见文件名和问题清单，
/// 测试用一个内存替身就能覆盖全部行为。
/// </para>
/// </summary>
public interface ISolutionCatalog
{
    /// <summary>solutions 目录的绝对路径。列不出来或目录不存在时也如实给出（界面要显示它）。</summary>
    string Folder { get; }

    /// <summary>
    /// 目录里的全部 .sol 文件名（不含路径，按名称排序）。目录不存在返回空清单——
    /// 部署机上没有 solutions 目录本身就是要显示的事实，不是异常。
    /// </summary>
    IReadOnlyList<string> ListSolutionFiles();

    /// <summary>
    /// 读某个方案的名册。文件名是 <see cref="ListSolutionFiles"/> 给出的那种（不含路径）。
    /// 失败抛异常且消息是人话（<see cref="SolutionManifestReader"/> 的契约）。
    /// </summary>
    SolutionManifest ReadManifest(string fileName);

    /// <summary>
    /// F-68 契约校验：配方按名字引用的模块是否真的在它声明的方案里。
    /// 返回给人看的问题清单，空 = 通过。方案缺失/损坏也算不通过（fail-closed）。
    /// </summary>
    IReadOnlyList<string> CheckContract(RecipeConfig recipe);
}

/// <summary><see cref="ISolutionCatalog"/> 的文件系统实现。</summary>
/// <param name="assetRoot">资产根（exe 目录）——配方里的相对 <c>solutionPath</c> 以它为基准解析</param>
public sealed class SolutionCatalog(string assetRoot) : ISolutionCatalog
{
    private string? _folder;

    /// <inheritdoc />
    public string Folder
    {
        get
        {
            // 惰性解析一次：目录可能尚不存在，不能在构造期碰盘（WPF 构造函数零副作用）。
            _folder ??= Path.Combine(assetRoot, "Config", "solutions");
            return _folder;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListSolutionFiles()
    {
        try
        {
            if (!Directory.Exists(Folder)) return [];
            return Directory.GetFiles(Folder, "*.sol")
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Cast<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录读不了与"目录里没有方案"在界面上都表现为空清单；
            // 差别由 Folder 显示 + 调用方提示补足，这里不抛——导入区不是关键路径。
            return [];
        }
    }

    /// <inheritdoc />
    public SolutionManifest ReadManifest(string fileName)
    {
        var safe = Path.GetFileName(fileName);
        var path = Path.Combine(Folder, safe);
        return SolutionManifestReader.Read(path);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> CheckContract(RecipeConfig recipe)
    {
        var configured = recipe.Vm.SolutionPath;
        if (string.IsNullOrWhiteSpace(configured))
            return ["未配置流程文件路径（vm.solutionPath）"];

        var path = RecipeConfig.ResolveAsset(configured, assetRoot);
        return RecipeContractValidator.Validate(recipe, path);
    }
}
