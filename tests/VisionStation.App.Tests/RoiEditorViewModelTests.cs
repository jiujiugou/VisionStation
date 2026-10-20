using System.IO;
using VisionStation.App.Abstractions;
using VisionStation.App.Services.Coordinates;
using VisionStation.App.ViewModels;
using VisionStation.Core.Models;
using VisionStation.Service.Recipes;

namespace VisionStation.App.Tests;

/// <summary>
/// ROI 保存的事务边界。
///
/// 保存不是“把当前列表写出去”这么简单：写盘失败时，内存配方必须保持旧值，
/// 否则界面显示已保存、磁盘上还是旧配方，重启后改动凭空消失。
/// </summary>
public class RoiEditorViewModelTests
{
    [Fact]
    public async Task SaveToFile_WhenStoreSucceeds_UpdatesRecipeAndShowsSuccess()
    {
        var recipe = CreateRecipe();
        var store = new FakeRecipeStore();
        var dialog = new FakeDialog();
        var vm = new RoiEditorViewModel(new CoordinateMapper(), recipe, store, dialog);

        var roi = vm.Rois[0];
        roi.Name = "ROI-new";
        roi.ImageX = 42;
        roi.ImageY = 43;

        await vm.SaveToFileCommand.ExecuteAsync(null);

        Assert.Equal(1, store.SaveCount);
        Assert.NotNull(store.Saved);
        Assert.NotSame(recipe, store.Saved);
        Assert.Equal("ROI-new", store.Saved!.Rois[0].Name);
        Assert.Equal(42, store.Saved.Rois[0].X);
        Assert.Equal(43, store.Saved.Rois[0].Y);

        Assert.Equal("ROI-new", recipe.Rois[0].Name);
        Assert.Equal(42, recipe.Rois[0].X);
        Assert.Equal(1, dialog.InfoCount);
        Assert.Equal(0, dialog.ErrorCount);
        Assert.Contains("saved.json", dialog.LastInfoMessage);
    }

    [Fact]
    public async Task SaveToFile_WhenStoreFails_LeavesRecipeUnchangedAndShowsError()
    {
        var recipe = CreateRecipe();
        var originalRois = recipe.Rois;
        var store = new FakeRecipeStore { Failure = new IOException("disk full") };
        var dialog = new FakeDialog();
        var vm = new RoiEditorViewModel(new CoordinateMapper(), recipe, store, dialog);

        vm.Rois[0].Name = "ROI-new";
        vm.Rois[0].ImageX = 99;

        await vm.SaveToFileCommand.ExecuteAsync(null);

        Assert.Equal(1, store.SaveCount);
        Assert.Same(originalRois, recipe.Rois);
        Assert.Equal("Old", recipe.Rois[0].Name);
        Assert.Equal(1, dialog.ErrorCount);
        Assert.Equal(0, dialog.InfoCount);
        Assert.Contains("disk full", dialog.LastErrorMessage);
    }

    private static RecipeConfig CreateRecipe() => new()
    {
        Rois =
        {
            new RoiData { Name = "Old", X = 1, Y = 2, Width = 3, Height = 4 }
        }
    };

    private sealed class FakeRecipeStore : IRecipeStore
    {
        public int SaveCount { get; private set; }
        public RecipeConfig? Saved { get; private set; }
        public Exception? Failure { get; set; }

        public Task<string> SaveAsync(RecipeConfig recipe, CancellationToken ct = default)
        {
            SaveCount++;
            Saved = recipe;

            return Failure is null
                ? Task.FromResult(@"C:\recipes\saved.json")
                : Task.FromException<string>(Failure);
        }
    }

    private sealed class FakeDialog : IDialogService
    {
        public int InfoCount { get; private set; }
        public int ErrorCount { get; private set; }
        public string LastInfoMessage { get; private set; } = "";
        public string LastErrorMessage { get; private set; } = "";

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task ShowInfoAsync(string title, string message)
        {
            InfoCount++;
            LastInfoMessage = message;
            return Task.CompletedTask;
        }

        public Task ShowErrorAsync(string title, string message)
        {
            ErrorCount++;
            LastErrorMessage = message;
            return Task.CompletedTask;
        }
    }
}
