using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Service;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// R-101 补充：标定值 ≤ 0 时配方必须判无效。
/// 依据：Calibration.PixelToMm 直接做除法，0 会产出 Infinity，尺寸判定全盘失真且无告警。
/// </summary>
public class RecipeCalibrationTests
{
    private static HealthResult Check(RecipeConfig recipe)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs_cal_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var hc = new HealthChecker(new DeviceManager(), Path.Combine(dir, "a.db"), dir, "", recipe);
        return hc.RunAll().First(r => r.Item == "配方文件");
    }

    [Fact]
    public void R101_ZeroPixelPerMillimeter_MustFail()
    {
        var recipe = new RecipeConfig { Name = "零标定" };
        recipe.Calibration.PixelPerMillimeter = 0;
        recipe.Rois.Add(new RoiData { Name = "R", Width = 100, Height = 100 });
        recipe.Tools.Add(new ToolConfigData { Type = "Dimension", Roi = "R" });

        var r = Check(recipe);

        Assert.False(r.OK, $"标定值为 0 仍判配方有效，px→mm 换算会产出 Infinity（F-101）。Detail={r.Detail}");
    }

    [Fact]
    public void R101_NegativePixelPerMillimeter_MustFail()
    {
        var recipe = new RecipeConfig { Name = "负标定" };
        recipe.Calibration.PixelPerMillimeter = -15;
        recipe.Rois.Add(new RoiData { Name = "R", Width = 100, Height = 100 });
        recipe.Tools.Add(new ToolConfigData { Type = "Dimension", Roi = "R" });

        Assert.False(Check(recipe).OK, "负标定值必须判配方无效");
    }

    [Fact]
    public void R101_UnknownToolType_MustFail()
    {
        var recipe = new RecipeConfig { Name = "未知工具" };
        recipe.Rois.Add(new RoiData { Name = "R", Width = 100, Height = 100 });
        recipe.Tools.Add(new ToolConfigData { Type = "NoSuchTool", Roi = "R" });

        Assert.False(Check(recipe).OK, "工具类型不在白名单时必须判配方无效（否则该工具被静默跳过）");
    }
}
