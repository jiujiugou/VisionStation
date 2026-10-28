using VisionStation.Core.Models;

namespace VisionStation.Service.Recipes;

/// <summary>
/// 配方 ↔ 方案 的契约校验闸（F-68，2026-10-28 落地）。
///
/// <para>
/// <b>它防的是什么</b>：配方 JSON 声称配某个 .sol（<c>vm.solutionPath</c>），
/// 并且按名字引用方案里的模块（<c>vm.outputs[].module</c>）。这个契约在此闸落地前
/// 只活在文档和人的脑子里——实证案例：<c>default-2448.json</c> 指着的
/// <c>washer-2448.sol</c> 是个没有检测模块的空壳，配对断了很久没人发现，
/// 因为运行时根本不读 module 字段（解析只用 label/role/metric）。
/// </para>
///
/// <para>
/// <b>挂点</b>：① 启动自检「检测引擎」项（<c>HealthChecker.CheckEngine</c>）——
/// 激活配方的契约断了，自检红、联锁拦自动运行，程序仍可启动（能进界面修配置）；
/// ② 配方页「切换为激活配方」（<c>RecipePageViewModel.SwitchAsync</c>）——
/// 目标配方契约不通过就不写激活指针。
/// </para>
///
/// <para>
/// <b>为什么 module 运行时零消费还要拦</b>：module 是"这份 JSON 真的是配这个方案"的
/// 唯一可机读证据。引用着方案里不存在的模块，说明配对大概率错了——错配对的直接后果是
/// 报文段顺序对不上，解析静默错位。宁可在启动时红，不在产线上错。
/// </para>
/// </summary>
public static class RecipeContractValidator
{
    /// <summary>
    /// 校验一份配方与它的方案文件是否契约一致。
    ///
    /// <para><paramref name="solutionPath"/> 必须是<b>已解析的绝对路径</b>
    /// （调用方用 <see cref="RecipeConfig.ResolveAsset"/> 解析；本方法不做路径推断）。</para>
    /// <para><b>永不抛异常</b>：所有失败（文件缺失/损坏/名册读不出）都变成问题清单里的一条，
    /// 因为调用方是自检和切换闸——它们要的是"能不能过 + 为什么"，不是异常栈。</para>
    /// </summary>
    /// <returns>给人看的问题清单；空 = 契约成立</returns>
    public static IReadOnlyList<string> Validate(RecipeConfig recipe, string solutionPath)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            problems.Add("未配置流程文件路径（vm.solutionPath）");
            return problems;
        }

        if (!File.Exists(solutionPath))
        {
            problems.Add($"流程文件不存在：{solutionPath}");
            return problems;
        }

        SolutionManifest manifest;
        try
        {
            manifest = SolutionManifestReader.Read(solutionPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            problems.Add($"方案无法解析（{solutionPath}）：{ex.Message}");
            return problems;
        }

        if (recipe.Vm.Outputs.Count == 0)
        {
            problems.Add("配方没有声明任何输出映射（vm.outputs）");
            return problems;
        }

        // 契约面：outputs[].module 按"显示名"引用方案模块（tools/RecipeGen 与现有配方都这么写）。
        // 同时认 Name：有些现场手写的配方抄的是类型名，一样说明"我知道这个模块是谁"。
        var known = manifest.Modules
            .Select(m => m.DisplayName)
            .Concat(manifest.Modules.Select(m => m.Name))
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var missing = recipe.Vm.Outputs
            .Select(o => o.Module.Trim())
            .Where(m => m.Length > 0 && !known.Contains(m))
            .Distinct()
            .ToList();

        if (missing.Count > 0)
        {
            problems.Add(
                "输出引用的模块在方案里不存在：" + string.Join("、", missing) +
                $"（方案 {manifest.SolutionName} 的名册共 {manifest.Modules.Count} 个模块）" +
                "——是否在 VisionMaster 里改过模块名，或选错了方案文件？");
        }

        return problems;
    }
}
