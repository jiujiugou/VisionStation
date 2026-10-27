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
///
/// <para>
/// <b>定位</b>：配方持久化的唯一落点（单一真相源）。调用方是配方页 VM 与组合根
/// （组合根其实只用它的 <see cref="ActivePointerFile"/> 常量去读指针）；
/// 它**不认识配置文件、不认识引擎**，只做"把 RecipeConfig 读写到某个目录"。
/// 本类不读 appsettings：目录在构造时就由 <c>AppPaths.RecipeFolder</c> 算好传进来。
/// </para>
///
/// <para>
/// <b>三级回退，但只有一级在本类里</b>（这点最容易读错）：
/// 完整顺序是 磁盘 <c>Config/recipes/&lt;.active 指向的那份&gt;</c> →
/// 磁盘 <c>Recipe:Path</c> 指向的默认文件 → <b>程序集内嵌的 default.json</b>。
/// 后两级都在 App 层的 <c>AppHost.LoadRecipe</c> 里，本类只负责第一级的"目录/指针/文件"。
/// 所以"配方文件全没了还能起来"这件事不是这里保证的，别在本类找兜底代码。
/// </para>
///
/// <para>
/// <b>坑：路径基准是 AppContext.BaseDirectory（bin / publish 目录），不是源码目录。</b>
/// 运行期读写的都是 exe 旁边那份副本，所以：
/// 改了 <c>src/VisionStation.App/Config/recipes/*.json</c> 或 appsettings.json
/// **必须重新构建**才会被复制到 bin（工程里配的是 <c>PreserveNewest</c>）；
/// 反过来，程序自己保存的配方写在 bin 里，若源码里那份被改得更晚，
/// 下次构建就会用源码版本覆盖现场调好的配方 —— 换型前先确认在改哪一份。
/// </para>
///
/// <para>
/// <b>只认声明了 <c>engine</c> 的 JSON</b>（判据见 <c>RecipeConfig.HasEngineDeclaration</c>）：
/// 只要能反序列化就当配方，config 目录里任何一个 json 都会被误读；
/// 而"说不出自己用哪个引擎的配方"本来就不该被加载。
/// 真正"只认 engine = Vm、其余直接抛"的判断不在本类，而在 App 层的
/// <c>InspectionEngineFactory</c>（配方写了未知引擎名会在启动期失败）—— 这是刻意分开的：
/// 存储层不该替运行时决定支持哪些引擎，但也绝不允许静默换成 VM：
/// 静默回落会让现场以为在跑 A、其实在跑 B，判据错了还一切显示正常，这种事故要查一整天。
/// </para>
///
/// <para>
/// <b>归属</b>：DI 单例，活到进程结束。调用线程以 UI 线程为主（配方页按钮命令）；
/// <see cref="SaveAsync"/> 内部是异步文件 IO，其余方法都是同步短 IO，会短暂占住调用线程。
/// 并发保存不靠锁，靠"每次用唯一临时文件名 + FileMove 覆盖"——
/// 两个保存同时发生的结果是"后完成的赢"，不会写出半个文件。
/// </para>
///
/// <para>
/// <b>失败行为</b>：分层。<see cref="SaveAsync"/> / <see cref="SetActiveAsync"/> 失败<b>抛</b>
/// （保存不能假装成功，否则操作员以为改了、实际没落盘）；
/// <see cref="LoadAsync"/> / <see cref="ListAsync"/> 失败返回 null / 跳过并记日志
/// （一份坏文件不该让整个配方页打不开）；备份与指针写入失败只记警告
/// （它们是保险，不是保存的前提）。
/// </para>
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

    /// <summary>配方目录的绝对路径（构造时 GetFullPath 过，之后不再拼相对路径）。</summary>
    private readonly string _folder;

    /// <summary>日志。可空：单测里不需要日志时不必造。备份/指针/坏文件这些"次要失败"只走它。</summary>
    private readonly ILogger<JsonRecipeStore>? _logger;

    /// <param name="folder">配方目录（不存在时首次保存会创建）。传相对路径会按当前目录解析成绝对路径 —— 组合根传的已经是绝对路径</param>
    public JsonRecipeStore(string folder, ILogger<JsonRecipeStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = Path.GetFullPath(folder);
        _logger = logger;
    }

    /// <summary>配方目录（绝对路径）。界面只用来显示，不要拿它去拼别的路径。</summary>
    public string Folder => _folder;

    /// <summary>
    /// 当前激活的配方文件名。
    /// 指针文件优先；指针指向的文件已被手工删掉时，退到目录里按文件名排序的第一份 ——
    /// 不因为"少了一个指针"就让整个软件没有配方可用。
    ///
    /// <para>
    /// 注意这个兜底的副作用：<see cref="SaveAsync"/> 就是拿它决定写哪个文件的。
    /// 指针丢了而目录里有别的配方时，"保存"会覆盖按名排序的第一份。
    /// 换句话说保存的目标由"当前激活项"决定，而不是由界面上选中的那个决定。
    /// </para>
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

    /// <summary>
    /// 保存配方，返回最终写入的绝对路径。
    ///
    /// <para>
    /// 目标文件 = 当前激活的那份；一份都没有时落到 <c>default.json</c>。
    /// 也就是说保存**不会新建第二份配方** —— 想"另存为"得先放一个文件进目录再切指针，
    /// 这是当前实现的边界，别指望点保存会长出一份新配方。
    /// </para>
    /// <para>
    /// 副作用（按顺序）：改 <c>UpdatedAt</c>（由存储层盖章，调用方别自己维护时间戳，
    /// 漏写一次就永远是旧时间）→ 备份旧文件 → 写唯一临时文件 → 原子替换 → 写 .active 指针。
    /// 指针跟着走是刻意的：否则出现"存到了 A、指针还指着 B"，下次启动装的是旧配方。
    /// </para>
    /// <para>
    /// 失败行为：抛。写不进去就是写不进去，不能返回一个假路径让界面显示"已保存"。
    /// 取消语义：<paramref name="ct"/> 只作用于序列化阶段，且在 Move 之前再查一次 ——
    /// 保证取消不会留下半截配方；文件替换本身是不可取消的原子动作。
    /// 线程：调用方在 UI 线程，await 落在异步 IO 上，不会冻结界面。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 列出目录下所有能解析的配方（配方页左栏）。
    ///
    /// 单个文件解析失败<b>跳过并记警告</b>，不抛 —— 一份手工改坏的 json
    /// 不该让整个配方页打不开（那就连"切回能用的那份"都做不到了）。
    /// 声明没写 <c>engine</c> 的文件会被静默忽略（不是坏文件，只是不是配方）。
    /// 返回的是快照：顺序按文件名排序，<c>IsActive</c> 由当前指针推出。
    /// 线程：同步 IO，UI 线程调用时会短暂阻塞（目录只有十几份，可接受）。
    /// </summary>
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

    /// <summary>
    /// 按文件名加载一份配方。文件不存在、不是配方、解析炸了，一律返回 null。
    ///
    /// <para>
    /// 入参只取文件名部分（<c>Path.GetFileName</c>）：这是防路径穿越的那道闸 ——
    /// 配方名可能来自界面输入或 .active 内容，<c>"..\..\appsettings.json"</c>
    /// 不该能读到配方目录之外去。
    /// </para>
    /// <para>
    /// 失败行为：返回 null + 记警告，不抛。调用方（切换配方）据此提示"这份配方读不出来"，
    /// 由它决定要不要退回原来那份 —— 兜底策略不写在本类里。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 把某份配方设为当前（只写 .active 指针，不复制/不改那份文件）。
    ///
    /// 前置条件：目标文件必须已存在 —— 不存在就抛 <see cref="FileNotFoundException"/>，
    /// 而不是先写指针让下次启动去发现"指针指向空气"。这条校验是刻意的：
    /// 切换配方是换型动作，静默失败会让操作员以为换好了、实际装着旧配方在跑。
    /// 线程：同步短 IO，UI 线程调用可接受（写一个几十字节的文本文件）。
    /// </summary>
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

    /// <summary>
    /// 读一个文件并反序列化。不是配方（没声明 engine）时返回 null。
    /// 读盘/解析的异常**不在这里吞**：由 ListAsync 逐文件 catch、由 LoadAsync 统一 catch，
    /// 这样"跳过坏文件"和"返回 null"两种语义不会被混在一起。
    /// </summary>
    private RecipeConfig? ReadRecipe(string path)
    {
        var json = File.ReadAllText(path);

        // 没声明 engine = 这份文件不是配方（或还是 schemaVersion 1 的旧配方）。
        // 判据本身在 Core，组合根用的是同一个 —— 两处判据不同会出现
        // "列表里能看见、启动时加载不了"这种自相矛盾。
        if (!RecipeConfig.HasEngineDeclaration(json)) return null;

        return JsonSerializer.Deserialize<RecipeConfig>(json, Options);
    }

    /// <summary>
    /// 读 .active 指针，返回文件名（已剥掉任何目录成分）。读不到/空文件/异常都返回 null，
    /// 让调用方退到"按名排序第一份"——指针是加速器，不是唯一真相。
    /// 异常这里只记 Debug：文件被占用这类情况下一启动就会遇到，记 Warning 会刷满日志。
    /// </summary>
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

    /// <summary>
    /// 写 .active 指针（同样先写 .tmp 再 Move，避免断电留下半截文件名）。
    /// 失败只记警告不抛：配方本体已经写成功了，指针丢了下次启动退到"第一份"，
    /// 这个降级可以接受；为了指针写不进去而报"保存失败"反而误导。
    /// </summary>
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
