using VisionStation.Core.Models;
using VisionStation.Service.Recipes;

namespace VisionStation.UnitTests;

/// <summary>
/// 导入落盘（F-67 <c>SaveNewAsync</c>）的行为契约。
///
/// <para>三件必须钉住的事：新文件真的写了、**不碰激活指针**（导入不悄悄变成当前配方）、
/// 同名拒绝（覆盖会毁掉现场改好的 label/顺序且没有撤销）。</para>
/// </summary>
public class RecipeImportStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly JsonRecipeStore _store;

    public RecipeImportStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vs-recipegen-" + Guid.NewGuid().ToString("N"));
        _store = new JsonRecipeStore(_dir, logger: null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static RecipeConfig Sample(string name = "垫圈A") => new()
    {
        Name = name,
        Note = "报文段顺序（分隔符 ;）：第1段=逻辑1。",
        Sensor = { Width = 2448, Height = 2048 },
        Vm =
        {
            SolutionPath = @"Config\solutions\4.0_V4.2.0.sol",
            Procedure = "流程1",
            Outputs =
            {
                new VmOutputMapping { Module = "圆查找1", Label = "外径", Role = "Measure" },
                new VmOutputMapping { Module = "BLOB分析1", Label = "有无", Role = "Defect" },
            },
        },
    };

    [Fact]
    public async Task SaveNew_WritesFile_DoesNotTouchPointer()
    {
        await _store.SaveNewAsync("垫圈A.json", Sample());

        Assert.True(File.Exists(Path.Combine(_dir, "垫圈A.json")));
        // 契约是"不写激活指针"（导入不悄悄变成当前配方）。
        // 注意不能用 ActiveName 判断：目录里只有一份配方时它有"回退到唯一配方"的既有语义（F-54）。
        Assert.False(File.Exists(Path.Combine(_dir, JsonRecipeStore.ActivePointerFile)));
    }

    [Fact]
    public async Task SaveNew_AppendsJsonExtension_WhenMissing()
    {
        var path = await _store.SaveNewAsync("垫圈B", Sample());

        Assert.True(File.Exists(path));
        Assert.EndsWith(".json", Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveNew_RefusesOverwrite_WithFriendlyError()
    {
        await _store.SaveNewAsync("垫圈A.json", Sample("第一份"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.SaveNewAsync("垫圈A.json", Sample("第二份")));

        Assert.Contains("垫圈A.json", ex.Message);
        // 第一份内容必须原封不动：覆盖会毁掉现场改好的 label/顺序
        var kept = await _store.LoadAsync("垫圈A.json");
        Assert.NotNull(kept);
        Assert.Equal("第一份", kept!.Name);
    }

    [Fact]
    public async Task SaveNew_FlattensPathInput_NeverEscapesFolder()
    {
        // 带目录成分的输入取末段文件名——既不炸，也绝不可能写出配方目录之外
        var path = await _store.SaveNewAsync(@"..\..\evil.json", Sample("逃逸尝试"));

        Assert.StartsWith(_dir, path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("evil.json", Path.GetFileName(path));
    }

    [Fact]
    public async Task SaveNew_RoundTrips_IncludingNote()
    {
        // note 是"报文段顺序"的唯一记载（F-13 教训：文档错比代码错更难发现，所以更要留得住）。
        // 2026-10-28 之前 RecipeConfig 没有 Note 属性，这一轮往返会把 note 静默丢掉。
        await _store.SaveNewAsync("垫圈A.json", Sample());

        var loaded = await _store.LoadAsync("垫圈A.json");

        Assert.NotNull(loaded);
        Assert.Equal("垫圈A", loaded!.Name);
        Assert.Equal(2448, loaded.Sensor.Width);
        Assert.Equal(@"Config\solutions\4.0_V4.2.0.sol", loaded.Vm.SolutionPath);
        Assert.Contains("第1段=逻辑1", loaded.Note);
        Assert.Equal("Vm", loaded.Engine);
        // outputs 的顺序就是报文段的顺序，导入一轮回来必须原样——错一位就是"外径"那列装了"有无"的值。
        Assert.Collection(loaded.Vm.Outputs,
            o => Assert.Equal("外径", o.Label),
            o => Assert.Equal("有无", o.Label));
        Assert.True(RecipeConfig.HasEngineDeclaration(
            File.ReadAllText(Path.Combine(_dir, "垫圈A.json"))));
    }

    [Fact]
    public async Task SaveNew_ListsTheNewRecipe_WithoutWritingPointer()
    {
        await _store.SaveNewAsync("垫圈A.json", Sample());

        var list = await _store.ListAsync();

        var row = Assert.Single(list);
        Assert.Equal("垫圈A.json", row.FileName);
        Assert.Equal(2, row.OutputCount);

        // "导入不改当前配方"的证据只能是**指针文件不存在**。
        // 不能拿 row.IsActive 当证据：目录里只有一份配方时，ActiveName 有"指针缺失就退到
        // 按名排序第一份"的既有语义（F-54，为的是少一个指针也不至于没配方可用），
        // 于是单配方目录里它必然显示为当前 —— 这是回退规则的产物，不是导入动了指针。
        Assert.False(File.Exists(Path.Combine(_dir, JsonRecipeStore.ActivePointerFile)));

        // 反过来把回退语义钉住：真要"不标当前"，得靠目录里还有第二份配方。
        await _store.SaveNewAsync("垫圈B.json", Sample("垫圈B"));
        var afterSecond = await _store.ListAsync();
        Assert.Equal(2, afterSecond.Count);
        Assert.Single(afterSecond, r => r.IsActive);
    }
}
