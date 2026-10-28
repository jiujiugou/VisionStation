using System.IO.Compression;
using System.Xml.Linq;

namespace VisionStation.Service.Recipes;

/// <summary>方案名册里的一条模块（<c>Name</c> 是类型标识，<c>DisplayName</c> 是画布上的名字）。</summary>
public sealed record SolutionModule(string Name, string DisplayName);

/// <summary>
/// 从 .sol 方案包解析出来的「权威名册」——方案里到底有哪些模块。
///
/// <para>
/// <b>为什么它存在</b>（F-68 的地基）：配方 JSON 与 .sol 之间的契约只有
/// 「outputs[].module ↔ 方案里的模块显示名」这一条，而在校验闸落地之前，
/// 这个契约只活在文档和人的脑子里，没有任何机制校验——
/// 结果 <c>default-2448.json</c> 指着的 <c>washer-2448.sol</c> 是个没有检测模块的空壳，
/// 断了很久没人发现。名册可机读（.sol 是 ZIP，<c>SolutionFile/VmServer.xml</c> 是明文），
/// 所以契约可以变成代码防线。
/// </para>
/// <para>
/// <b>它能回答什么、不能回答什么</b>（2026-10-28 实证）：能回答"有哪些模块"；
/// 不能回答"报文订阅顺序"（那是通信管理/发送数据模块的配置，在二进制参数里，
/// 明文里搜不到）。所以导入草稿的顺序永远是"预填 + 待人工核对"，绝不装作机器知道。
/// </para>
/// </summary>
/// <param name="SolutionName">方案自报的名字（VmServer.xml 的 SolutionName；取不到用文件名）</param>
/// <param name="Procedures">流程显示名清单（如「流程1」）</param>
/// <param name="Modules">全部模块，按文档序（实证与模块 Index 一致）</param>
public sealed record SolutionManifest(
    string SolutionName,
    IReadOnlyList<string> Procedures,
    IReadOnlyList<SolutionModule> Modules)
{
    /// <summary>
    /// 基础设施模块：它们是流程的"水电煤"，不是检测内容，永远不该出现在 outputs 里。
    /// 与 tools/RecipeGen v1 是同一套清单——两处规则必须一致，改一处必须改另一处。
    /// </summary>
    private static readonly HashSet<string> Infrastructure =
    [
        "ImageSourceModule", "GlobalScript", "SendDatasModule", "CommManagerModule", "IMVSProcessControl",
    ];

    /// <summary>
    /// 导入草稿的输出候选（F-67）。
    ///
    /// <para>三条预填规则（与 tools/RecipeGen v1 完全一致）：</para>
    /// <list type="number">
    /// <item>剔除基础设施模块（图像源/脚本/发送/通信/流程控制）——它们不是检测项；</item>
    /// <item>存图模块（SaveImage）从候选中摘出、经 <paramref name="saveImage"/> 单独带回——
    ///     它是"可选的末段图像路径"，不是判定项，是否订阅由人决定；</item>
    /// <item>逻辑汇总（AndModule/逻辑*）提到最前——真实报文的第 1 段通常是总判定。</item>
    /// </list>
    /// <para>
    /// <b>顺序仍然只是预填</b>：机器拿不到订阅顺序（见类型注释），调用方必须在界面上
    /// 让人核对。这里不提供"顺序已确认"这种表达——它不存在。
    /// </para>
    /// </summary>
    public IReadOnlyList<SolutionModule> DraftCandidates(out SolutionModule? saveImage)
    {
        var candidates = Modules.Where(m => !Infrastructure.Contains(m.Name)).ToList();

        saveImage = candidates.FirstOrDefault(m => m.Name == "SaveImage");
        if (saveImage is not null)
            candidates.Remove(saveImage);

        var logic = candidates.FirstOrDefault(m =>
            m.Name.Contains("AndModule", StringComparison.OrdinalIgnoreCase) ||
            m.DisplayName.StartsWith("逻辑", StringComparison.Ordinal));
        if (logic is not null)
        {
            candidates.Remove(logic);
            candidates.Insert(0, logic);
        }

        return candidates;
    }
}

/// <summary>
/// 读 .sol 方案包的名册。失败抛异常且消息是人话——调用方（自检、导入、切换闸）
/// 都直接把异常消息展示给工程师。
/// </summary>
public static class SolutionManifestReader
{
    /// <summary>
    /// 解析方案名册。
    ///
    /// <para>
    /// <b>不用 <c>ZipArchive.GetEntry</c></b>：它按原始名精确匹配，而不同 VM 版本导出的
    /// 分隔符/大小写有差异（tools/RecipeGen 实测踩过）。统一规范成小写正斜杠再比，
    /// 容得住导出工具的脾气。
    /// </para>
    /// </summary>
    /// <exception cref="FileNotFoundException">方案文件不存在</exception>
    /// <exception cref="InvalidDataException">不是有效的 ZIP 容器，或包里没有 VmServer.xml（不是 VM 4.x 导出的方案）</exception>
    public static SolutionManifest Read(string solutionPath)
    {
        using var zip = ZipFile.OpenRead(solutionPath);

        var manifest = zip.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').Equals("SolutionFile/VmServer.xml", StringComparison.OrdinalIgnoreCase));
        if (manifest is null)
            throw new InvalidDataException("包里没有 SolutionFile/VmServer.xml——这可能不是 VisionMaster 4.x 导出的方案。");

        var doc = XDocument.Load(manifest.Open());

        var solutionName = doc.Descendants("Solution").FirstOrDefault()?.Attribute("SolutionName")?.Value
                           ?? Path.GetFileNameWithoutExtension(solutionPath);

        var procedures = doc.Descendants("Procedure")
            .Select(p => p.Attribute("DisplayName")?.Value ?? "")
            .Where(n => n.Length > 0)
            .ToList();

        // 名册 = 文档里出现的全部 Module 元素。同名只留一个（名册不该有重名，防御一下）。
        var modules = doc.Descendants("Module")
            .Select(m => new SolutionModule(
                Name: m.Attribute("Name")?.Value ?? "",
                DisplayName: m.Attribute("DisplayName")?.Value ?? ""))
            .Where(m => m.Name.Length > 0)
            .GroupBy(m => m.DisplayName)
            .Select(g => g.First())
            .ToList();

        return new SolutionManifest(solutionName, procedures, modules);
    }
}
