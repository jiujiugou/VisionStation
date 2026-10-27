using Microsoft.Extensions.Configuration;
using VisionStation.App.Composition;

using System.IO;

namespace VisionStation.App.Tests;

/// <summary>
/// S5（W-14）：配置绑定与校验的唯一入口。这组测试钉住三条纪律：
/// 缺项 → 默认值；值非法 → 报错带键名；不静默回落。
/// </summary>
public class AppOptionsTests
{
    private static IConfiguration Config(Dictionary<string, string?> kv)
        => new ConfigurationBuilder().AddInMemoryCollection(kv).Build();

    [Fact]
    public void Bind_空配置_全部走默认值()
    {
        var options = AppOptionsBinder.Bind(Config(new Dictionary<string, string?>()));

        Assert.Equal("All", options.Storage.Mode);
        Assert.Equal(2000, options.Trigger.TimerIntervalMs);
        Assert.Equal(30, options.Housekeeping.KeepRecordsDays);
    }

    [Fact]
    public void Bind_合法值_原样绑定()
    {
        var options = AppOptionsBinder.Bind(Config(new Dictionary<string, string?>
        {
            ["Trigger:TimerIntervalMs"] = "5000",
            ["Storage:Mode"] = "NGOnly",
            ["Housekeeping:KeepRecordsDays"] = "7",
        }));

        Assert.Equal(5000, options.Trigger.TimerIntervalMs);
        Assert.Equal("NGOnly", options.Storage.Mode);
        Assert.Equal(7, options.Housekeeping.KeepRecordsDays);
    }

    [Fact]
    public void Bind_存储模式不合法_报错()
    {
        var ex = Assert.Throws<AppOptionsValidationException>(() =>
            AppOptionsBinder.Bind(Config(new Dictionary<string, string?> { ["Storage:Mode"] = "Everything" })));

        Assert.Contains("Storage:Mode", ex.Message);
    }

    [Fact]
    public void Bind_数字写错_报错带键名而不是裸FormatException()
    {
        var ex = Assert.Throws<AppOptionsValidationException>(() =>
            AppOptionsBinder.Bind(Config(new Dictionary<string, string?>
            {
                ["Trigger:TimerIntervalMs"] = "两秒",
                ["Housekeeping:KeepRecordsDays"] = "三十",
            })));

        // 两个错误都在：一条报错能看到全部配置问题，不用改一个跑一次
        Assert.Contains("Trigger:TimerIntervalMs", ex.Message);
        Assert.Contains("Housekeeping:KeepRecordsDays", ex.Message);
    }

    [Fact]
    public void Bind_间隔非正数_报错()
    {
        var ex = Assert.Throws<AppOptionsValidationException>(() =>
            AppOptionsBinder.Bind(Config(new Dictionary<string, string?>
            {
                ["Trigger:TimerIntervalMs"] = "0",
            })));

        Assert.Contains("Trigger:TimerIntervalMs", ex.Message);
    }

    [Fact]
    public void Bind_布尔写错_报错()
    {
        var ex = Assert.Throws<AppOptionsValidationException>(() =>
            AppOptionsBinder.Bind(Config(new Dictionary<string, string?>
            {
                ["Plc:Enabled"] = "yes",
            })));

        Assert.Contains("Plc:Enabled", ex.Message);
    }

    [Fact]
    public void Bind_管家阈值非法_Service层构造校验仍然生效()
    {
        // HousekeepingOptions 自己有构造校验（F-113）；binder 读出的非法值要能穿透到那里
        var ex = Assert.ThrowsAny<Exception>(() =>
            AppOptionsBinder.Bind(Config(new Dictionary<string, string?>
            {
                ["Housekeeping:KeepRecordsDays"] = "0",
            })));

        Assert.IsNotAssignableFrom<AppOptionsValidationException>(ex);   // 不是 binder 报的，是 record 自己报的
    }

    [Fact]
    public void Bind_数据根_默认是exe目录()
    {
        var options = AppOptionsBinder.Bind(Config(new Dictionary<string, string?>()));
        Assert.Equal(AppContext.BaseDirectory, options.DataRoot);
    }

    [Fact]
    public void Bind_数据根_相对参数按exe目录解析成绝对路径()
    {
        var options = AppOptionsBinder.Bind(
            Config(new Dictionary<string, string?>()), dataRootOverride: "PortableData");

        Assert.True(Path.IsPathRooted(options.DataRoot));
        Assert.EndsWith("PortableData", options.DataRoot, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_数据根_坏参数报错退出而不是悄悄回落()
    {
        var ex = Assert.Throws<AppOptionsValidationException>(() =>
            AppOptionsBinder.Bind(
                Config(new Dictionary<string, string?>()), dataRootOverride: "bad\0path"));

        Assert.Contains("--data-root", ex.Message);
    }
}
