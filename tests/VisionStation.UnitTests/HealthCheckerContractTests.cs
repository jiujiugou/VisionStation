using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Service;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// 三步失效分析法 · 阶段3（v2 重做版）：按目标行为编写，当前代码不满足即红。
/// 红 = 缺陷的可执行证据（FAIL 态），修复后转绿，不在本文件内改断言。
/// </summary>

/// <summary>R-101 / F-101：配方自检必须校验工具可用性，不能只看 Tools.Count>0。</summary>
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
        var hc = new HealthChecker(new DeviceManager(), Path.Combine(dir, "a.db"), dir, "", recipe);
        return hc.RunAll().First(r => r.Item == "配方文件");
    }

    [Fact]
    public void R101_EmptyToolType_MustFail()
    {
        var recipe = new RecipeConfig { Name = "空类型" };
        recipe.Tools.Add(new ToolConfigData { Type = "", Roi = "" });

        var r = Check(recipe);

        Assert.False(r.OK, $"工具类型为空仍被判为配方有效（F-101 静默错误判定）。Detail={r.Detail}");
    }

    [Fact]
    public void R101_UnknownRoiReference_MustFail()
    {
        var recipe = new RecipeConfig { Name = "悬空ROI" };
        recipe.Rois.Clear();                                  // 配方里没有任何 ROI
        recipe.Tools.Add(new ToolConfigData { Type = "Dimension", Roi = "ROI-矩形1" });

        var r = Check(recipe);

        Assert.False(r.OK, $"工具引用了不存在的 ROI 仍判有效（F-101）。Detail={r.Detail}");
    }

    [Fact]
    public void R101_ValidRecipe_MustPass()
    {
        var recipe = new RecipeConfig { Name = "正常配方" };
        recipe.Rois.Add(new RoiData { Name = "ROI-矩形1", X = 0, Y = 0, Width = 100, Height = 100 });
        recipe.Tools.Add(new ToolConfigData { Type = "Dimension", Roi = "ROI-矩形1" });

        var r = Check(recipe);

        Assert.True(r.OK, "合法配方不得被误判为无效");
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

        var hc = new HealthChecker(new DeviceManager(), db, dir, "", null);
        var r = hc.RunAll().First(x => x.Item == "数据库");

        Assert.False(r.OK, $"库文件被独占仍报 OK（F-102：判据只查 File.Exists）。Detail={r.Detail}");
    }

    [Fact]
    public void R102_ReadableDbFile_MustPass()
    {
        var dir = Tmp();
        var db = Path.Combine(dir, "visionstation.db");
        File.WriteAllText(db, "SQLite format 3\0");

        var hc = new HealthChecker(new DeviceManager(), db, dir, "", null);
        var r = hc.RunAll().First(x => x.Item == "数据库");

        Assert.True(r.OK, "可正常打开的库文件不得误报");
    }
}

/// <summary>R-103 / F-103：模板自检必须验证可解码，不能只验证文件存在。</summary>
public class TemplateValidationTests
{
    private static string Tmp()
    {
        var d = Path.Combine(Path.GetTempPath(), "vs_tpl_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void R103_CorruptTemplate_MustFail()
    {
        var dir = Tmp();
        var tpl = Path.Combine(dir, "template.bmp");
        File.WriteAllText(tpl, "这不是图像，只是改了扩展名的文本");   // 存在但不可解码

        var hc = new HealthChecker(new DeviceManager(), Path.Combine(dir, "a.db"), dir, tpl, null);
        var r = hc.RunAll().First(x => x.Item == "模板文件");

        Assert.False(r.OK, $"损坏模板仍报 OK（F-103：停机点后移到运行期）。Detail={r.Detail}");
    }

    [Fact]
    public void R103_MissingTemplate_MustFail()
    {
        var dir = Tmp();
        var hc = new HealthChecker(new DeviceManager(), Path.Combine(dir, "a.db"), dir,
            Path.Combine(dir, "not-exist.bmp"), null);
        var r = hc.RunAll().First(x => x.Item == "模板文件");

        Assert.False(r.OK, "模板缺失必须 FAIL");
    }
}
