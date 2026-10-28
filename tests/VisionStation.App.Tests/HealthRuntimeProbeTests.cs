using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using VisionStation.App;
using VisionStation.App.Composition;
using VisionStation.App.Services;
using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Service;
using Xunit;

namespace VisionStation.App.Tests;

/// <summary>
/// F-49：启动自检第 4 项「运行时缺失」必须是真的能失败的检查。
///
/// <para>
/// 这几条用例**从组合根走**（<see cref="ServiceCollectionExtensions.AddAppServices"/> 建出的真实
/// <see cref="HealthChecker"/>），不是直接 new 一个传参——缺陷本身就是"组合根不传这个参数"，
/// 直接构造被测对象会把要验的那一段绕过去。
/// </para>
///
/// <para>
/// 探测对象的选择理由见 <c>ServiceCollectionExtensions.ProbeEngineRuntime</c> 的注释：
/// 本程序不加载 VM 的 SDK，所以"本机没装 VisionMaster"对它不是故障；
/// 真正的运行时前置是**结果推送入口**（链路是否启用 + 端口能否监听）。
///
/// <para>
/// 挂 <see cref="LocalPortCollection"/>：其中"端口被占用"一条会真的绑定端口并**保持占用**，
/// 与同样走临时端口的 <see cref="PlcIntegrationTests"/> 并行会互相抢号。
/// </para>
/// </summary>
[Collection(LocalPortCollection.Name)]
public class HealthRuntimeProbeTests
{
    private static string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs_runtime_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 一份合法到能走到"运行时"那一段的 VM 配方。
    ///
    /// <para>
    /// F-68（2026-10-28）起第 4 项多了一道契约校验：要读 .sol 的 ZIP 名册核对
    /// <c>vm.outputs[].module</c>。所以夹具不能再是"随便几个字节的非空文件"——
    /// 那样会先被"方案无法解析"拦下，这几条用例就再也测不到运行时那一段了
    /// （错的绿灯变红，但红的原因不对）。
    /// </para>
    /// </summary>
    private static RecipeConfig VmRecipe(string root)
    {
        var sol = WriteSolution(Path.Combine(root, "flow.sol"), "圆查找1");

        var recipe = new RecipeConfig
        {
            Name = "VM 配方",
            Engine = EngineNames.Vm,
            Vm = new VmRecipeData { SolutionPath = sol }
        };
        recipe.Vm.Outputs.Add(new VmOutputMapping { Module = "圆查找1", Label = "外径", Role = "Measure" });
        return recipe;
    }

    /// <summary>最小可解析的 .sol：ZIP + <c>SolutionFile/VmServer.xml</c>（结构同 <c>SolFixture</c>）。</summary>
    private static string WriteSolution(string path, string moduleDisplayName)
    {
        var xml = "<Solution SolutionName=\"测试方案\"><Procedure DisplayName=\"流程1\" />" +
                  "<Module Name=\"CircleFind\" DisplayName=\"" + moduleDisplayName + "\" Index=\"0\" /></Solution>";

        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using var entry = zip.CreateEntry("SolutionFile/VmServer.xml").Open();
        var bytes = Encoding.UTF8.GetBytes(xml);
        entry.Write(bytes, 0, bytes.Length);
        return path;
    }

    /// <summary>走组合根建容器，只取第 4 项「检测引擎」的结果</summary>
    private static HealthResult EngineItem(AppOptions options, string root)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new DeviceManager());   // 自检第 1 项只读它的统计，不需要真设备
        services.AddSingleton(options);
        services.AddSingleton(VmRecipe(root));

        ServiceCollectionExtensions.AddAppServices(services, new AppPaths(options), options);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IHealthChecker>()
            .RunAll().Single(r => r.Item == "检测引擎");
    }

    [Fact]
    public void 运行时探测_推送链路未启用_第4项必须报不健康()
    {
        var root = NewRoot();
        // 引擎是 Vm（推送驱动）却把链路关了 = 本次运行永远收不到结果。
        // 修复前：组合根不传 engineRuntimeProblem，这一项只查 .sol 在不在，报绿 —— F-49 的假绿灯。
        var options = new AppOptions
        {
            DataRoot = root,
            VmLink = new VmLinkOptions(Enabled: false, Port: 9100, BindAddress: "127.0.0.1", StallTimeoutSeconds: 10)
        };

        var item = EngineItem(options, root);

        Assert.False(item.OK, $"推送链路没开却报自检通过（F-49）。Detail={item.Detail}");
        Assert.Contains("VmLink:Enabled=false", item.Error);
    }

    [Fact]
    public void 运行时探测_推送入口端口被占用_第4项必须报不健康()
    {
        var root = NewRoot();

        // 占住一个端口扮演"上一个实例没退干净 / 别的软件抢了端口"。
        // 明确 ExclusiveAddressUse：不依赖平台默认值，保证探测侧一定绑不上。
        var squatter = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        squatter.Start();
        try
        {
            var port = ((IPEndPoint)squatter.LocalEndpoint).Port;
            var options = new AppOptions
            {
                DataRoot = root,
                VmLink = new VmLinkOptions(Enabled: true, Port: port, BindAddress: "127.0.0.1", StallTimeoutSeconds: 10)
            };

            var item = EngineItem(options, root);

            Assert.False(item.OK, $"推送入口被占用却报自检通过。Detail={item.Detail}");
            Assert.Contains("无法监听", item.Error);
        }
        finally
        {
            squatter.Stop();
        }
    }

    [Fact]
    public void 运行时探测_链路正常时不得误杀()
    {
        // 正例：防止把探测写成"永远 FAIL"（那是另一种假信号）。
        var root = NewRoot();

        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var options = new AppOptions
        {
            DataRoot = root,
            VmLink = new VmLinkOptions(Enabled: true, Port: port, BindAddress: "127.0.0.1", StallTimeoutSeconds: 10)
        };

        var item = EngineItem(options, root);

        Assert.True(item.OK, $"链路可用却被判故障：{item.Detail} / {item.Error}");
    }
}
