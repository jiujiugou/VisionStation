using System.Diagnostics;
using VisionStation.App.Services.Restart;

namespace VisionStation.App.Tests;

/// <summary>
/// 一键重启的启动信息构造（F-69）。
///
/// <para>
/// 只测 BuildStartInfo（纯函数）——Launch 会真的起进程，不进单测。
/// 核心契约：<b>参数原样重放</b>。重启不带原始命令行的话，<c>--data-root</c>
/// 会在"重启后"悄悄丢失，数据根回落成 exe 目录——数据其实在别处，但没人查得出来
/// （与看门狗参数重放 App.StartWatchdog 同一条教训）。
/// </para>
/// </summary>
public class RestartLauncherTests
{
    [Fact]
    public void BuildStartInfo_ReplaysOriginalArgs_InOrder()
    {
        var psi = RestartLauncher.BuildStartInfo(@"C:\app\VisionStation.App.exe",
            ["--data-root", @"D:\My Data\VS", "--other=1"]);

        Assert.Equal(@"C:\app\VisionStation.App.exe", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(["--data-root", @"D:\My Data\VS", "--other=1"], psi.ArgumentList);
    }

    [Fact]
    public void BuildStartInfo_NoArgs_EmptyArgumentList()
    {
        var psi = RestartLauncher.BuildStartInfo(@"C:\app\VisionStation.App.exe", Array.Empty<string>());

        Assert.Empty(psi.ArgumentList);
    }

    [Fact]
    public void BuildStartInfo_SpaceInPath_IsKeptAsSingleArgument()
    {
        // ArgumentList 的存在意义：含空格的路径必须仍是"一个参数"，
        // 不能像手拼字符串那样被拆成两截（那是 StartWatchdog 踩过的坑）。
        var psi = RestartLauncher.BuildStartInfo(@"C:\My App\VisionStation.App.exe",
            [@"D:\My Data\VS"]);

        Assert.Single(psi.ArgumentList);
        Assert.Equal(@"D:\My Data\VS", psi.ArgumentList[0]);
    }
}
