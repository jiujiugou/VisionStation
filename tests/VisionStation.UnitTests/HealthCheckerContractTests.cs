using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Service;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// 三步失效分析法 · 阶段3：按目标行为编写，当前代码不满足即红。
/// 红 = 缺陷的可执行证据（FAIL 态），修复后转绿，不在本文件内改断言。
///
/// 2026-10-23 改造：判据从「工具/ROI 引用」换成「引擎 + 引擎参数」。
/// 被删掉的旧用例（EmptyToolType / UnknownRoiReference）测的是 rois/tools 两段，
/// 那两段已随自研工具链一起移除，对应的失效模式不复存在。
/// 新增的三条覆盖现在真正会出现的静默错误：引擎名写错、.sol/模型没配、输出映射为空。
/// </summary>

/// <summary>R-101 / F-101：配方自检必须校验引擎可用性，不能只看文件能不能解析。</summary>
public class RecipeValidationTests
{
    private static string Tmp()
    {
        var d = Path.Combine(Path.GetTempPath(), "vs_recipe_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static HealthResult Check(RecipeConfig recipe)
    {
        var dir = Tmp();
        var hc = new HealthChecker(new DeviceManager(), Path.Combine(dir, "a.db"), dir, recipe, dir);
        return hc.RunAll().First(r => r.Item == "配方文件");
    }

    [Fact]
    public void R101_UnknownEngine_MustFail()
    {
        var recipe = new RecipeConfig { Name = "引擎名写错", Engine = "VisionMaster" };   // 正确值是 Vm

        var r = Check(recipe);

        Assert.False(r.OK, $"未知引擎仍被判为配方有效（F-101 静默错误判定）。Detail={r.Detail}");
    }

    [Fact]
    public void R101_VmWithoutOutputs_MustFail()
    {
        // 有 .sol、有流程名，但一项输出都没配 —— 流程会跑，结论一条都没有。
        // 这是最危险的一种"看起来配好了"：检测永远返回空报告，而空报告按 NG 处理，
        // 现场会以为"产品全不合格"而不是"配置没写"。
        var recipe = new RecipeConfig { Name = "VM无输出", Engine = EngineNames.Vm };

        var r = Check(recipe);

        Assert.False(r.OK, $"VM 配方没有任何输出映射仍判有效（F-101）。Detail={r.Detail}");
    }

    [Fact]
    public void R101_ValidRecipe_MustPass()
    {
        // VM 配方的四项必填：流程文件、流程名、图像源模块、至少一条输出映射。
        // 少任何一项都会被 R101_VmWithoutOutputs_MustFail 那一类用例拦下，
        // 所以这条"正例"必须把它们都配齐，否则它测的是反例。
        var recipe = new RecipeConfig { Name = "正常配方", Engine = EngineNames.Vm };
        recipe.Vm.SolutionPath = @"Config\recipes\default.json";
        recipe.Vm.Procedure = "流程1";
        recipe.Vm.ImageSourceModule = "图像源1";
        recipe.Vm.Outputs.Add(new VmOutputMapping { Module = "模块1", Label = "值1" });

        var r = Check(recipe);

        Assert.True(r.OK, $"合法配方不得被误判为无效。Detail={r.Detail} / {r.Error}");
    }
}

/// <summary>R-102 / F-102：数据库可用性必须实测可打开，不能以文件存在为通过条件。</summary>
public class DbAvailabilityTests
{
    private static string Tmp()
    {
        var d = Path.Combine(Path.GetTempPath(), "vs_db_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void R102_ExclusivelyLockedDbFile_MustFail()
    {
        var dir = Tmp();
        var db = Path.Combine(dir, "visionstation.db");
        File.WriteAllText(db, "SQLite format 3\0");          // 文件存在
        using var exclusive = new FileStream(db, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var hc = new HealthChecker(new DeviceManager(), db, dir, null, dir);
        var r = hc.RunAll().First(x => x.Item == "数据库");

        Assert.False(r.OK, $"库文件被独占仍报 OK（F-102：判据只查 File.Exists）。Detail={r.Detail}");
    }

    [Fact]
    public void R102_ReadableDbFile_MustPass()
    {
        var dir = Tmp();
        var db = Path.Combine(dir, "visionstation.db");
        File.WriteAllText(db, "SQLite format 3\0");

        var hc = new HealthChecker(new DeviceManager(), db, dir, null, dir);
        var r = hc.RunAll().First(x => x.Item == "数据库");

        Assert.True(r.OK, "可正常打开的库文件不得误报");
    }
}

/// <summary>
/// R-103 / F-103：引擎资源自检。
///
/// 检查对象从「模板文件」换成「引擎资源」（VM 的 .sol 流程文件）：
/// 模板是自研 PresenceTool 的输入，随工具链一起移除；
/// 现在引擎的输入是流程文件，缺了同样跑不起来，同样必须在启动期就报。
///
/// 刻意没保留「改了扩展名的文本文件」那条：.sol 是压缩包，
/// 想在不加载它的前提下判断"是不是真的能跑"只能靠猜，猜错比不查更糟。
/// 这一条改由运行期兜底：引擎加载失败会返回 NG 并把异常原文带出来。
/// </summary>
public class EngineAssetTests
{
    private static string Tmp()
    {
        var d = Path.Combine(Path.GetTempPath(), "vs_engine_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static RecipeConfig VmRecipe(string solutionPath) => new()
    {
        Name = "VM 配方",
        Engine = EngineNames.Vm,
        Vm = new VmRecipeData
        {
            SolutionPath = solutionPath,
            Procedure = "流程1",
            ImageSourceModule = "图像源1",
            Outputs = { new VmOutputMapping { Module = "圆查找1", Label = "外径" } }
        }
    };

    [Fact]
    public void R103_MissingSolution_MustFail()
    {
        var dir = Tmp();
        var hc = new HealthChecker(new DeviceManager(), Path.Combine(dir, "a.db"), dir,
            VmRecipe(Path.Combine(dir, "not-exist.sol")), dir);

        var r = hc.RunAll().First(x => x.Item == "检测引擎");

        Assert.False(r.OK, "流程文件缺失必须 FAIL");
    }

    [Fact]
    public void R103_EmptySolutionFile_MustFail()
    {
        var dir = Tmp();
        var sol = Path.Combine(dir, "empty.sol");
        File.WriteAllBytes(sol, Array.Empty<byte>());

        var hc = new HealthChecker(new DeviceManager(), Path.Combine(dir, "a.db"), dir, VmRecipe(sol), dir);

        var r = hc.RunAll().First(x => x.Item == "检测引擎");

        Assert.False(r.OK, "空文件必须 FAIL：0 字节的 .sol 显然是拷贝中断");
    }

    [Fact]
    public void R103_PresentSolution_MustPass()
    {
        var dir = Tmp();
        var sol = Path.Combine(dir, "washer.sol");
        File.WriteAllBytes(sol, new byte[] { 1, 2, 3, 4 });

        var hc = new HealthChecker(new DeviceManager(), Path.Combine(dir, "a.db"), dir, VmRecipe(sol), dir);

        var r = hc.RunAll().First(x => x.Item == "检测引擎");

        Assert.True(r.OK, $"真实存在的流程文件被误判：{r.Detail} / {r.Error}");
        Assert.Contains(EngineNames.Vm, r.Detail);
    }

    [Fact]
    public void R103_EngineRuntimeMissing_MustFail()
    {
        // 文件齐了但运行时没有（本机没装 VM）：这条必须报出来，
        // 否则会出现"自检全绿、第一帧才发现引擎是替身"的最坏情况。
        var dir = Tmp();
        var sol = Path.Combine(dir, "washer.sol");
        File.WriteAllBytes(sol, new byte[] { 1, 2, 3, 4 });

        var hc = new HealthChecker(new DeviceManager(), Path.Combine(dir, "a.db"), dir,
            VmRecipe(sol), dir, engineRuntimeProblem: "本机未安装 VisionMaster SDK");

        var r = hc.RunAll().First(x => x.Item == "检测引擎");

        Assert.False(r.OK);
        Assert.Contains("VisionMaster SDK", r.Error);
    }
}
