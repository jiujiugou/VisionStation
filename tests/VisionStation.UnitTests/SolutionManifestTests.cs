using System.IO.Compression;
using VisionStation.Core.Models;
using VisionStation.Service.Recipes;

namespace VisionStation.UnitTests;

/// <summary>
/// .sol 名册解析与契约校验（F-68）的行为契约。
///
/// <para>
/// fixture 是程序里现造的最小 ZIP（结构与真方案同构：<c>SolutionFile/VmServer.xml</c>），
/// 不依赖仓库里的真方案文件——真方案 7.9MB 且随 VM 导出变化，
/// 测试要的是"结构规则"，不是某次导出的快照。
/// </para>
/// </summary>
public class SolutionManifestTests : IDisposable
{
    private readonly string _dir;
    private readonly string _solPath;

    public SolutionManifestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vs-solman-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _solPath = Path.Combine(_dir, "test.sol");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>造一个最小 .sol（结构见 <see cref="SolFixture"/>，与自检那几条用例共用同一份夹具）。</summary>
    private void WriteSolution(params (string Name, string DisplayName)[] modules)
        => SolFixture.Write(_solPath, modules);

    [Fact]
    public void Read_ReturnsRosterInDocumentOrder()
    {
        WriteSolution(
            ("ImageSourceModule", "图像源1"),
            ("BLOBAnalysis", "BLOB分析1"),
            ("AndModule", "逻辑1"));

        var manifest = SolutionManifestReader.Read(_solPath);

        Assert.Equal("测试方案", manifest.SolutionName);
        Assert.Equal(["流程1"], manifest.Procedures);
        Assert.Equal(3, manifest.Modules.Count);
        Assert.Equal("图像源1", manifest.Modules[0].DisplayName);
        Assert.Equal("BLOBAnalysis", manifest.Modules[1].Name);
    }

    [Fact]
    public void DraftCandidates_StripsInfra_MovesLogicFirst_ExtractsSaveImage()
    {
        WriteSolution(
            ("ImageSourceModule", "图像源1"),
            ("BLOBAnalysis", "BLOB分析1"),
            ("SaveImage", "输出图像1"),
            ("AndModule", "逻辑1"));

        var manifest = SolutionManifestReader.Read(_solPath);
        var candidates = manifest.DraftCandidates(out var saveImage);

        // 基础设施（图像源）不在候选里；存图模块单列；逻辑汇总提前
        Assert.Equal(["逻辑1", "BLOB分析1"], candidates.Select(c => c.DisplayName));
        Assert.NotNull(saveImage);
        Assert.Equal("输出图像1", saveImage!.DisplayName);
    }

    [Fact]
    public void Read_NotAZip_ThrowsInvalidData()
    {
        File.WriteAllText(_solPath, "这不是 zip");
        Assert.Throws<InvalidDataException>(() => SolutionManifestReader.Read(_solPath));
    }

    [Fact]
    public void Read_ZipWithoutManifest_ThrowsInvalidData()
    {
        using (var stream = new FileStream(_solPath, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            zip.CreateEntry("别的目录/别的.xml");

        Assert.Throws<InvalidDataException>(() => SolutionManifestReader.Read(_solPath));
    }

    [Fact]
    public void Read_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => SolutionManifestReader.Read(_solPath));
    }

    // ══════════════════ 契约校验闸 ══════════════════

    private RecipeConfig Recipe(params string[] modules)
    {
        var recipe = new RecipeConfig
        {
            Name = "测试配方",
            Vm = { SolutionPath = _solPath },
        };
        foreach (var m in modules)
            recipe.Vm.Outputs.Add(new VmOutputMapping { Module = m, Label = m });
        return recipe;
    }

    [Fact]
    public void Contract_Passes_WhenModulesExist()
    {
        WriteSolution(("BLOBAnalysis", "BLOB分析1"));

        var problems = RecipeContractValidator.Validate(Recipe("BLOB分析1"), _solPath);

        Assert.Empty(problems);
    }

    [Fact]
    public void Contract_Fails_WhenModuleMissing_NamesIt()
    {
        // 空壳方案：只有图像源，配方却引用 BLOB分析1 —— washer-2448 空壳案的等价场景
        WriteSolution(("ImageSourceModule", "图像源1"));

        var problems = RecipeContractValidator.Validate(Recipe("BLOB分析1"), _solPath);

        Assert.Single(problems);
        Assert.Contains("BLOB分析1", problems[0]);
        Assert.Contains("测试方案", problems[0]);
    }

    [Fact]
    public void Contract_Fails_WhenSolutionFileMissing()
    {
        var problems = RecipeContractValidator.Validate(Recipe("任意"), Path.Combine(_dir, "不存在.sol"));

        Assert.Single(problems);
        Assert.Contains("不存在", problems[0]);
    }

    [Fact]
    public void Contract_Fails_WhenSolutionCorrupt()
    {
        File.WriteAllText(_solPath, "不是 zip");

        var problems = RecipeContractValidator.Validate(Recipe("任意"), _solPath);

        Assert.Single(problems);
        Assert.Contains("无法解析", problems[0]);
    }

    [Fact]
    public void Contract_Fails_WhenNoOutputs()
    {
        WriteSolution(("BLOBAnalysis", "BLOB分析1"));
        var recipe = new RecipeConfig { Name = "无输出", Vm = { SolutionPath = _solPath } };

        var problems = RecipeContractValidator.Validate(recipe, _solPath);

        Assert.Single(problems);
        Assert.Contains("输出映射", problems[0]);
    }
}

/// <summary>小幅流转助手：对象初始化器后再补一段配置。</summary>
internal static class RecipeTapExtensions
{
    public static T Tap<T>(this T item, Action<T> configure)
    {
        configure(item);
        return item;
    }
}
