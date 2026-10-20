using System.Text.Json;
using VisionStation.Core.Models;
using VisionStation.Service.Recipes;

namespace VisionStation.UnitTests;

/// <summary>
/// 配方保存的持久化契约。
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
        var path = Path.Combine(dir.Path, "recipe.json");
        var recipe = new RecipeConfig { Name = "测试配方" };
        recipe.Rois.Add(new RoiData
        {
            Name = "Presence",
            X = 1,
            Y = 2,
            Width = 3,
            Height = 4
        });

        var savedPath = await new JsonRecipeStore(path).SaveAsync(recipe);

        Assert.Equal(Path.GetFullPath(path), savedPath);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.True(doc.RootElement.TryGetProperty("rois", out var rois));
        Assert.False(doc.RootElement.TryGetProperty("Rois", out _));
        Assert.Equal("Presence", rois[0].GetProperty("name").GetString());
    }

    /// <summary>
    /// 首次部署时 Config/recipes 目录可能还不存在。
    /// 保存必须自己建目录，而不是把“目录不存在”留给操作员。
    /// </summary>
    [Fact]
    public async Task SaveAsync_CreatesMissingDirectory()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "nested", "recipe.json");

        await new JsonRecipeStore(path).SaveAsync(new RecipeConfig());

        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// 目标是目录时 Move 必然失败。
    /// 这里验证失败后临时文件被清掉，避免每次失败都在配方目录里留垃圾。
    /// </summary>
    [Fact]
    public async Task SaveAsync_WhenTargetIsDirectory_ThrowsAndCleansTempFile()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "recipe.json");
        Directory.CreateDirectory(target);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new JsonRecipeStore(target).SaveAsync(new RecipeConfig()));

        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
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
