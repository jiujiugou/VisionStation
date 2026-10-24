using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VisionStation.Core.Models;

namespace VisionStation.Service.Recipes;

/// <summary>
/// JSON 配方存储 —— 一个目录就是一组配方（S7 从单文件扩展而来）。
///
/// 目录约定：
///   Config/recipes/washer-1280.json     一份配方
///   Config/recipes/washer-2448.json     另一份配方
///   Config/recipes/.active              指针文件，内容 = 当前用哪份（文件名）
///   Config/recipes/.backup/*.json       保存前的自动备份，最多留 10 份
///
/// 为什么先写临时文件再 Move：
///   File.WriteAllText 会先截断目标文件。写到一半崩溃/断电时，配方可能变成空文件或半截 JSON；
///   App.LoadRecipe 遇到损坏配方会静默回退内嵌默认配方，操作员会带着错误标定继续跑。
///   临时文件 + 原子替换保证旧配方只在完整写入成功后才被替换。
///
/// 为什么保存前要备份：
///   ROI 编辑器点一下保存就覆盖原文件。现场把 ROI 拖歪、把阈值改错之后想退回去，
///   没有备份就只能靠记忆重调。备份比"小心一点"可靠。
/// </summary>
public sealed class JsonRecipeStore : IRecipeStore
{
    /// <summary>激活指针文件名（以点开头，不会被 *.json 扫到）</summary>
    public const string ActivePointerFile = ".active";

    /// <summary>备份目录名</summary>
    public const string BackupFolderName = ".backup";

    /// <summary>默认配方文件名（目录里没有激活项时，保存落在这里）</summary>
    private const string DefaultFileName = "default.json";

    /// <summary>保留的备份份数，超出删最旧的</summary>
    private const int BackupKeep = 10;

    /// <summary>
    /// 写文件用 camelCase（与现有 Config/recipes/*.json 保持一致）；
    /// 读文件大小写不敏感（现场手工编辑时大小写经常不一致）。
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _folder;
    private readonly ILogger<JsonRecipeStore>? _logger;

    /// <param name="folder">配方目录（不存在时首次保存会创建）</param>
    public JsonRecipeStore(string folder, ILogger<JsonRecipeStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = Path.GetFullPath(folder);
        _logger = logger;
    }

    public string Folder => _folder;

    /// <summary>
    /// 当前激活的配方文件名。
    /// 指针文件优先；指针指向的文件已被手工删掉时，退到目录里按文件名排序的第一份 ——
    /// 不因为"少了一个指针"就让整个软件没有配方可用。
    /// </summary>
    public string? ActiveName
    {
        get
        {
            var pointer = ReadPointer();
            if (pointer != null && File.Exists(Path.Combine(_folder, pointer)))
                return pointer;

            var first = ListFiles().FirstOrDefault();
            return first is null ? null : Path.GetFileName(first);
        }
    }

    public async Task<string> SaveAsync(RecipeConfig recipe, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        Directory.CreateDirectory(_folder);

        var fileName = ActiveName ?? DefaultFileName;
        var path = Path.Combine(_folder, fileName);

        // 保存时刻由存储层盖章：调用方（ROI 编辑器）只表达"保存这个配方"，
        // 不该自己去维护时间戳，漏写一次就永远是旧时间。
        recipe.UpdatedAt = DateTimeOffset.Now;

        BackupExisting(path, fileName);

        // 唯一临时文件：并发保存/上次中断留下的文件不会互相覆盖。
        var tempPath = Path.Combine(_folder, Path.GetRandomFileName() + ".tmp");
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
            File.Move(tempPath, path, overwrite: true);

            // 保存的这份就是当前要用的这份：指针跟着走，避免"存档存在 A、指针指着 B"。
            WritePointer(fileName);

            return path;
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

    public Task<IReadOnlyList<RecipeSummary>> ListAsync(CancellationToken ct = default)
    {
        var active = ActiveName;
        var list = new List<RecipeSummary>();

        foreach (var file in ListFiles())
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var recipe = ReadRecipe(file);
                if (recipe is null) continue;

                var fileName = Path.GetFileName(file);
                list.Add(new RecipeSummary(
                    recipe.Name,
                    fileName,
                    recipe.Sensor.Width,
                    recipe.Sensor.Height,
                    recipe.UpdatedAt,
                    recipe.Engine,
                    string.Equals(fileName, active, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex)
            {
                // 单个坏文件不该让整个配方列表打不开：跳过它，记一笔。
                _logger?.LogWarning(ex, "配方解析失败，已跳过: {File}", file);
            }
        }

        return Task.FromResult<IReadOnlyList<RecipeSummary>>(list);
    }

    public Task<RecipeConfig?> LoadAsync(string fileName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return Task.FromResult<RecipeConfig?>(null);

        // GetFileName 去掉任何目录成分：防止 "..\..\appsettings.json" 这类穿越。
        var path = Path.Combine(_folder, Path.GetFileName(fileName));
        if (!File.Exists(path)) return Task.FromResult<RecipeConfig?>(null);

        try
        {
            return Task.FromResult(ReadRecipe(path));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "配方加载失败: {File}", path);
            return Task.FromResult<RecipeConfig?>(null);
        }
    }

    public Task SetActiveAsync(string fileName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var safe = Path.GetFileName(fileName);
        var path = Path.Combine(_folder, safe);
        if (!File.Exists(path))
            throw new FileNotFoundException($"配方不存在：{safe}", path);

        WritePointer(safe);
        return Task.CompletedTask;
    }

    // ══════════════════ 内部 ══════════════════

    /// <summary>目录下所有配方文件（按文件名排序，保证"第一份"是稳定的）</summary>
    private IEnumerable<string> ListFiles()
    {
        if (!Directory.Exists(_folder)) yield break;

        foreach (var file in Directory.GetFiles(_folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            yield return file;
    }

    private RecipeConfig? ReadRecipe(string path)
    {
        var json = File.ReadAllText(path);

        // 没声明 engine = 这份文件不是配方（或还是 schemaVersion 1 的旧配方）。
        // 判据本身在 Core，组合根用的是同一个 —— 两处判据不同会出现
        // "列表里能看见、启动时加载不了"这种自相矛盾。
        if (!RecipeConfig.HasEngineDeclaration(json)) return null;

        return JsonSerializer.Deserialize<RecipeConfig>(json, Options);
    }

    private string? ReadPointer()
    {
        var path = Path.Combine(_folder, ActivePointerFile);
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(text) ? null : Path.GetFileName(text);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "读取配方指针失败: {Path}", path);
            return null;
        }
    }

    private void WritePointer(string fileName)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, ActivePointerFile);
        var temp = path + ".tmp";

        try
        {
            File.WriteAllText(temp, fileName);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // 指针写不进去不影响配方本身已保存成功，下次启动会退到"第一份"，记一笔即可。
            _logger?.LogWarning(ex, "写入配方指针失败: {Path}", path);
        }
    }

    /// <summary>把现有目标文件复制到 .backup；失败不阻断保存（备份是保险，不是前提）。</summary>
    private void BackupExisting(string path, string fileName)
    {
        if (!File.Exists(path)) return;

        try
        {
            var dir = Path.Combine(_folder, BackupFolderName);
            Directory.CreateDirectory(dir);

            var baseName = Path.GetFileNameWithoutExtension(fileName);
            // 精确到毫秒：连点两次保存（或程序化连续保存）时，秒级时间戳会互相覆盖，
            // 备份就变成了"只剩最后一次"，而那恰恰是最没用的那一份。
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
            File.Copy(path, Path.Combine(dir, $"{baseName}.{stamp}.json"), overwrite: true);
            PruneBackups(dir, baseName);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "配方备份失败（本次保存继续）: {Path}", path);
        }
    }

    /// <summary>只保留最近 BackupKeep 份备份，其余删掉（备份不能自己长成磁盘垃圾）。</summary>
    private void PruneBackups(string backupDir, string baseName)
    {
        try
        {
            var stale = Directory.GetFiles(backupDir, $"{baseName}.*.json")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .Skip(BackupKeep)
                .ToList();

            foreach (var file in stale)
                File.Delete(file);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "清理旧配方备份失败: {Dir}", backupDir);
        }
    }
}
