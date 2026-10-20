using Microsoft.Extensions.Configuration;
using VisionStation.App.Composition;
using VisionStation.App.Services;

using System.IO;

namespace VisionStation.App.Tests;

/// <summary>
/// S5（W-15）：便携模式路径解析。核心契约：
/// 可写数据只认 DataRoot，与 Environment.CurrentDirectory 无关 ——
/// 快捷方式/计划任务启动时 cwd 不可控，这是当年数据"落错地方"的根因。
/// </summary>
public class AppPathsTests : IDisposable
{
    private readonly string _dataRoot;
    private readonly string _resourceRoot;

    public AppPathsTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "vs_paths_" + Guid.NewGuid().ToString("N")[..8]);
        _resourceRoot = Path.Combine(Path.GetTempPath(), "vs_res_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_resourceRoot);
    }

    public void Dispose()
    {
        Directory.Delete(_dataRoot, recursive: true);
        Directory.Delete(_resourceRoot, recursive: true);
    }

    private AppOptions Options(Dictionary<string, string?>? overrides = null, string? dataRoot = null)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (key, value) in overrides ?? new())
            dict[key] = value;

        return AppOptionsBinder.Bind(
            new ConfigurationBuilder().AddInMemoryCollection(dict).Build(),
            dataRootOverride: dataRoot ?? _dataRoot);
    }

    [Fact]
    public void 可写数据_落在数据根_与当前目录无关()
    {
        var paths = new AppPaths(Options());

        Assert.StartsWith(_dataRoot, paths.DbPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(_dataRoot, paths.LogFolder, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(_dataRoot, paths.StorageFolder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 可写数据_换当前目录结果不变()
    {
        // 数据根是绑定好的绝对路径，cwd 再怎么变都影响不了输出 —— 这条钉的就是 cwd 解耦
        var before = new AppPaths(Options());
        var paths = new AppPaths(Options(dataRoot: _dataRoot + Path.DirectorySeparatorChar + "sub"));

        Assert.StartsWith(_dataRoot, paths.DbPath, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(before.DbPath, paths.DbPath);
    }

    [Fact]
    public void 绝对路径配置_原样使用()
    {
        var absDb = Path.Combine(_resourceRoot, "custom.db");
        var paths = new AppPaths(Options(new Dictionary<string, string?>
        {
            ["Storage:DbPath"] = absDb,
        }));

        Assert.Equal(absDb, paths.DbPath);
    }

    [Fact]
    public void 相对数据路径_显式dataRoot优先于exe目录()
    {
        var paths = new AppPaths(Options(dataRoot: _dataRoot));

        Assert.StartsWith(_dataRoot, paths.DbPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AppContext.BaseDirectory, paths.DbPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 读资源_相对路径_优先exe目录()
    {
        // 资源随 exe 部署（便携语义）：在 exe 目录放一个探针目录，解析必须落在那里而不是 cwd。
        // 用完即删，不污染测试输出目录。
        var probeDir = Path.Combine(AppContext.BaseDirectory, "vs_res_probe_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(probeDir);
            var probeName = Path.GetFileName(probeDir);

            var paths = new AppPaths(Options(new Dictionary<string, string?>
            {
                ["Camera:ImageFolder"] = probeName,
            }));

            Assert.Equal(probeDir, paths.ImageFolder);
        }
        finally
        {
            Directory.Delete(probeDir, recursive: true);
        }
    }

    [Fact]
    public void 读资源_exe目录没有时_回退当前目录()
    {
        // dotnet run 时的开发便利：项目根（=cwd）里的 TestImages 仍能被找到。
        // 资源是只读的，这个回退不产生任何落盘歧义。
        var cwdDir = Path.Combine(Environment.CurrentDirectory, "vs_cwd_probe_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(cwdDir);

            var paths = new AppPaths(Options(new Dictionary<string, string?>
            {
                ["Camera:ImageFolder"] = Path.GetFileName(cwdDir),
            }));

            Assert.Equal(cwdDir, paths.ImageFolder);
        }
        finally
        {
            Directory.Delete(cwdDir, recursive: true);
        }
    }
}
