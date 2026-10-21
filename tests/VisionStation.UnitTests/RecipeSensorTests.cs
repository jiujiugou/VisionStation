using VisionStation.Core.Models;

namespace VisionStation.UnitTests;

/// <summary>
/// 配方与画面尺寸的匹配校验（S7）。
///
/// 背景：ROI 是像素坐标、标定是 px/mm，两者都绑死在某个分辨率上。
/// 换相机而配方没跟着改时，程序不报错、不崩，只是安静地把 ROI 框到零件外面然后判 NG ——
/// 现场会先怀疑产品、再怀疑相机、最后才怀疑配置。这个校验的作用就是把这层沉默打破。
/// </summary>
public class RecipeSensorTests
{
    [Fact]
    public void CheckSensor_PassesWhenSizeMatches()
    {
        var recipe = Recipe(1280, 1024);

        Assert.Null(recipe.CheckSensor(1280, 1024));
    }

    [Fact]
    public void CheckSensor_ReportsBothSizesWhenMismatched()
    {
        var recipe = Recipe(1280, 1024);

        var reason = recipe.CheckSensor(2448, 2048);

        Assert.NotNull(reason);
        Assert.Contains("1280×1024", reason, StringComparison.Ordinal);
        Assert.Contains("2448×2048", reason, StringComparison.Ordinal);
        Assert.Contains("拒绝", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// 旧配方没有 sensor 段（升级前写的），不能因为缺字段就把它判死 —— 那是把升级变成停线。
    /// </summary>
    [Fact]
    public void CheckSensor_SkipsWhenUndeclared()
    {
        var recipe = new RecipeConfig { Name = "旧配方" };

        Assert.False(recipe.Sensor.IsDeclared);
        Assert.Null(recipe.CheckSensor(2448, 2048));
    }

    /// <summary>只写了一边（写错/漏写）不算声明，否则 0 会永远匹配不上任何画面。</summary>
    [Fact]
    public void CheckSensor_SkipsWhenOnlyOneSideDeclared()
    {
        var recipe = new RecipeConfig
        {
            Name = "半吊子",
            Sensor = new SensorSpec { Width = 1280 }
        };

        Assert.False(recipe.Sensor.IsDeclared);
        Assert.Null(recipe.CheckSensor(2448, 2048));
    }

    private static RecipeConfig Recipe(int width, int height) => new()
    {
        Name = "金属垫圈检测",
        Sensor = new SensorSpec { Width = width, Height = height }
    };
}
