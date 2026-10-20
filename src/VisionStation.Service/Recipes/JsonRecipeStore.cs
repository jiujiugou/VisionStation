using System.Text.Json;
using Microsoft.Extensions.Logging;
using VisionStation.Core.Models;

namespace VisionStation.Service.Recipes;

/// <summary>
/// JSON 配方存储。
///
/// 为什么先写临时文件再 Move：
///   File.WriteAllText 会先截断目标文件。写到一半崩溃/断电时，配方可能变成空文件或半截 JSON；
///   App.LoadRecipe 遇到损坏配方会静默回退内嵌默认配方，操作员会带着错误标定继续跑。
///   临时文件 + 原子替换保证旧配方只在完整写入成功后才被替换。
/// </summary>
public sealed class JsonRecipeStore : IRecipeStore
{
    /// <summary>配方文件用 camelCase，和 Config/recipes/default.json 保持一致。</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly ILogger<JsonRecipeStore>? _logger;

    public JsonRecipeStore(string path, ILogger<JsonRecipeStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _logger = logger;
    }

    public async Task<string> SaveAsync(RecipeConfig recipe, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory))
            directory = Directory.GetCurrentDirectory();

        Directory.CreateDirectory(directory);

        // 唯一临时文件：并发保存/上次中断留下的文件不会互相覆盖。
        var tempPath = Path.Combine(directory, Path.GetRandomFileName() + ".tmp");
        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, recipe, Options, ct)
                    .ConfigureAwait(false);
            }

            // 序列化完整成功后才替换旧文件；取消时不留下半截配方。
            ct.ThrowIfCancellationRequested();
            File.Move(tempPath, _path, overwrite: true);
            return _path;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException ex)
                {
                    _logger?.LogDebug(ex, "清理配方临时文件失败: {Path}", tempPath);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger?.LogDebug(ex, "清理配方临时文件失败: {Path}", tempPath);
                }
            }
        }
    }
}
