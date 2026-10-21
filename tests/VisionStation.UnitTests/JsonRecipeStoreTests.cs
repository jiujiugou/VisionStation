using System.Text.Json;
using VisionStation.Core.Models;
using VisionStation.Service.Recipes;

namespace VisionStation.UnitTests;

/// <summary>
/// 配方持久化的契约（S7：从单文件变成配方目录）。
///
/// 这些测试不碰 WPF：Service 层是 net8.0，落盘行为可以独立验证。
/// </summary>
public class JsonRecipeStoreTests
{
    /// <summary>
    /// 保存后必须是 camelCase，且能按原字段读回。
    /// 如果格式退回 PascalCase，App.LoadRecipe 仍能读（大小写不敏感），
    /// 但 default.json 与保存结果会长期混用两种风格，diff 和人工排查都会变吵。
    /// </summary>
    [Fact]
    public async Task SaveAsync_WritesCamelCaseJsonAndReturnsPath()
    {
        using var dir = new TempDir();
        var recipe = new RecipeConfig { Name = "测试配方" };
        recipe.Rois.Add(new RoiData
        {
            Name = "Presence",
            X = 1,
            Y = 2,
            Width = 3,
            Height = 4
        });

        var savedPath = await new JsonRecipeStore(dir.Path).SaveAsync(recipe);

        // 目录里没有 .active 时保存落到 default.json
        Assert.Equal(Path.Combine(dir.Path, "default.json"), savedPath);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(savedPath));
        Assert.True(doc.RootElement.TryGetProperty("rois", out var rois));
        Assert.False(doc.RootElement.TryGetProperty("Rois", out _));
        Assert.Equal("Presence", rois[0].GetProperty("name").GetString());
    }

    /// <summary>
    /// 保存时必须盖上时间戳，否则配方页永远显示"不知道多久没改过"。
    /// </summary>
    [Fact]
    public async Task SaveAsync_StampsUpdatedAt()
    {
        using var dir = new TempDir();
        var recipe = new RecipeConfig { Name = "盖章测试" };
        var before = DateTimeOffset.Now.AddSeconds(-1);

        await new JsonRecipeStore(dir.Path).SaveAsync(recipe);

        Assert.NotNull(recipe.UpdatedAt);
        Assert.True(recipe.UpdatedAt >= before);
    }

    /// <summary>
    /// 首次部署时 Config/recipes 目录可能还不存在。
    /// 保存必须自己建目录，而不是把“目录不存在”留给操作员。
    /// </summary>
    [Fact]
    public async Task SaveAsync_CreatesMissingDirectory()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "nested");

        await new JsonRecipeStore(folder).SaveAsync(new RecipeConfig());

        Assert.True(File.Exists(Path.Combine(folder, "default.json")));
    }

    /// <summary>
    /// 目标是目录时 Move 必然失败。
    /// 这里验证失败后临时文件被清掉，避免每次失败都在配方目录里留垃圾。
    /// </summary>
    [Fact]
    public async Task SaveAsync_WhenTargetIsDirectory_ThrowsAndCleansTempFile()
    {
        using var dir = new TempDir();
        // default.json 位置被一个目录占住 → File.Move 会失败
        Directory.CreateDirectory(Path.Combine(dir.Path, "default.json"));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new JsonRecipeStore(dir.Path).SaveAsync(new RecipeConfig()));

        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    /// <summary>
    /// 保存前必须把旧版本留一份。
    /// 现场把 ROI 拖歪、把阈值改错之后想退回去，没有备份就只能靠记忆重调。
    /// </summary>
    [Fact]
    public async Task SaveAsync_BacksUpPreviousVersion()
    {
        using var dir = new TempDir();
        var store = new JsonRecipeStore(dir.Path);

        await store.SaveAsync(new RecipeConfig { Name = "第一版" });
        await store.SaveAsync(new RecipeConfig { Name = "第二版" });

        var backups = Directory.GetFiles(Path.Combine(dir.Path, JsonRecipeStore.BackupFolderName), "*.json");
        Assert.Single(backups);

        // 备份里留的是"第一版"，也就是被覆盖掉的那一份
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(backups[0]));
        Assert.Equal("第一版", doc.RootElement.GetProperty("name").GetString());
    }

    /// <summary>
    /// 备份不能无限长：留最近 10 份，更早的删掉。
    /// </summary>
    [Fact]
    public async Task SaveAsync_KeepsAtMostTenBackups()
    {
        using var dir = new TempDir();
        var store = new JsonRecipeStore(dir.Path);

        for (var i = 0; i < 15; i++)
            await store.SaveAsync(new RecipeConfig { Name = $"版本{i}" });

        var backups = Directory.GetFiles(Path.Combine(dir.Path, JsonRecipeStore.BackupFolderName), "*.json");
        Assert.Equal(10, backups.Length);
    }

    /// <summary>
    /// 多配方：目录里放两份，列表要都能列出来，并标出哪份是激活的。
    /// </summary>
    [Fact]
    public async Task ListAsync_ListsAllRecipes_AndMarksActiveOne()
    {
        using var dir = new TempDir();
        await WriteRecipeAsync(dir.Path, "a-1280.json", "A 产品", 1280, 1024);
        await WriteRecipeAsync(dir.Path, "b-2448.json", "B 产品", 2448, 2048);

        var store = new JsonRecipeStore(dir.Path);
        await store.SetActiveAsync("b-2448.json");

        var list = await store.ListAsync();

        Assert.Equal(2, list.Count);
        Assert.Single(list, r => r.IsActive);
        Assert.Equal("b-2448.json", list.Single(r => r.IsActive).FileName);

        var a = list.Single(r => r.FileName == "a-1280.json");
        Assert.Equal(1280, a.Width);
        Assert.Equal(1024, a.Height);
    }

    /// <summary>
    /// 激活指针必须落盘：换个 store 实例（等于重启程序）还要认得同一份。
    /// </summary>
    [Fact]
    public async Task SetActiveAsync_PersistsAcrossInstances()
    {
        using var dir = new TempDir();
        await WriteRecipeAsync(dir.Path, "a.json", "A", 1280, 1024);
        await WriteRecipeAsync(dir.Path, "b.json", "B", 2448, 2048);

        await new JsonRecipeStore(dir.Path).SetActiveAsync("b.json");

        Assert.Equal("b.json", new JsonRecipeStore(dir.Path).ActiveName);
    }

    /// <summary>切换到一个不存在的配方必须抛异常，不能默默改指针。</summary>
    [Fact]
    public async Task SetActiveAsync_ThrowsWhenFileMissing()
    {
        using var dir = new TempDir();
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new JsonRecipeStore(dir.Path).SetActiveAsync("missing.json"));
    }

    /// <summary>
    /// 指针指向的文件被手工删掉后，不能整个软件没有配方可用 —— 退回目录里第一份。
    /// </summary>
    [Fact]
    public async Task ActiveName_FallsBackToFirstFile_WhenPointerIsDangling()
    {
        using var dir = new TempDir();
        await WriteRecipeAsync(dir.Path, "a.json", "A", 1280, 1024);
        await WriteRecipeAsync(dir.Path, "b.json", "B", 2448, 2048);
        await new JsonRecipeStore(dir.Path).SetActiveAsync("b.json");
        File.Delete(Path.Combine(dir.Path, "b.json"));

        Assert.Equal("a.json", new JsonRecipeStore(dir.Path).ActiveName);
    }

    /// <summary>目录穿越防护：LoadAsync 只认文件名，不能借 ../ 读到目录外的文件。</summary>
    [Fact]
    public async Task LoadAsync_RejectsPathTraversal()
    {
        using var dir = new TempDir();
        var outside = Path.Combine(dir.Path, "outside.json");
        await File.WriteAllTextAsync(outside, """{"name":"外部","tools":[{"type":"Presence"}]}""");
        var folder = Path.Combine(dir.Path, "recipes");
        Directory.CreateDirectory(folder);

        var recipe = await new JsonRecipeStore(folder).LoadAsync("../outside.json");

        Assert.Null(recipe);
    }

    /// <summary>写测试配方用的序列化选项（静态复用：每次 new 会被 CA1869 判定为无谓分配）</summary>
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static async Task WriteRecipeAsync(string folder, string fileName, string name, int w, int h)
    {
        var recipe = new RecipeConfig
        {
            Name = name,
            Sensor = new SensorSpec { Width = w, Height = h }
        };
        recipe.Tools.Add(new ToolConfigData { Type = "Presence", Enabled = true });

        var json = JsonSerializer.Serialize(recipe, CamelCaseOptions);
        await File.WriteAllTextAsync(Path.Combine(folder, fileName), json);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "vsrecipe_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
