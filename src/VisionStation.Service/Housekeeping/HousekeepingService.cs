using Microsoft.Extensions.Logging;
using VisionStation.Storage;

namespace VisionStation.Service.Housekeeping;

/// <summary>
/// 管家服务阈值 —— 由 App 组合根从 appsettings.json 解析后传入。
/// Service 层不直接读配置文件（与 HealthChecker 收明确参数的约定一致），
/// 也方便单测直接构造。
/// </summary>
public sealed record HousekeepingOptions
{
    public int MaxMemoryMb { get; init; }
    public int KeepRecordsDays { get; init; }
    public int ImageRetentionDays { get; init; }
    public long MaxImageFolderBytes { get; init; }
    public int MemoryCheckIntervalMs { get; init; }
    public int DatabaseCleanIntervalMs { get; init; }

    /// <summary>
    /// 构造即校验：阈值来自配置文件，填错必须启动期就炸，
    /// 不能等到半夜管家把当天所有图片删干净才被发现。
    /// 这里用抛异常而不是回落默认值——静默回落会让配置错误永远看不见。
    /// </summary>
    public HousekeepingOptions(
        int maxMemoryMb,
        int keepRecordsDays,
        int imageRetentionDays,
        long maxImageFolderBytes,
        int memoryCheckIntervalMs,
        int databaseCleanIntervalMs)
    {
        if (maxMemoryMb <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxMemoryMb), maxMemoryMb, "内存上限必须大于 0");

        if (keepRecordsDays < 1)
            throw new ArgumentOutOfRangeException(nameof(keepRecordsDays), keepRecordsDays, "记录保留天数必须 ≥ 1（0 = 删光全部记录）");

        // 图片比记录先消失 → 数据库里的记录会指向不存在的图，质检追溯断链
        if (imageRetentionDays < keepRecordsDays)
            throw new ArgumentOutOfRangeException(nameof(imageRetentionDays), imageRetentionDays,
                $"图片保留天数({imageRetentionDays}) 不得小于记录保留天数({keepRecordsDays})，否则记录会指向已删除的图片");

        if (maxImageFolderBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxImageFolderBytes), maxImageFolderBytes, "图片容量上限必须大于 0");

        if (memoryCheckIntervalMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(memoryCheckIntervalMs), memoryCheckIntervalMs, "内存检查间隔必须大于 0");

        if (databaseCleanIntervalMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(databaseCleanIntervalMs), databaseCleanIntervalMs, "数据库清理间隔必须大于 0");

        MaxMemoryMb = maxMemoryMb;
        KeepRecordsDays = keepRecordsDays;
        ImageRetentionDays = imageRetentionDays;
        MaxImageFolderBytes = maxImageFolderBytes;
        MemoryCheckIntervalMs = memoryCheckIntervalMs;
        DatabaseCleanIntervalMs = databaseCleanIntervalMs;
    }

    /// <summary>
    /// 默认值（与 appsettings.json 中的 Housekeeping 段一致）：
    /// 内存上限 500MB、记录保留 30 天、图片保留 30 天、图片目录上限 1000MB、
    /// 内存检查每 60 秒、数据库清理每 1 小时。
    ///
    /// 这六个数只在这里出现一次 —— appsettings 缺项时由绑定器取它，
    /// 两处各抄一份的下场是有一份忘了改。
    /// </summary>
    public static HousekeepingOptions Default => new(
        500, 30, 30, 1000L * 1024 * 1024, 60_000, 3_600_000);
}

/// <summary>
/// 后台管家的生命周期把手。
///
/// <para>
/// 空接口不是偷懒：注册时用它，是因为消费方（<c>AppHost</c>）需要在启动阶段
/// "解析一次就让它跑起来"（构造函数自己起 Timer），关闭时又必须能拿到它去释放 ——
/// 有接口才有这个"两个时机都能摸到同一个实例"的定位点。没有任何业务方法是有意的：
/// 管家的工作全靠自己的定时器驱动，不接受外部调用。
/// </para>
/// </summary>
public interface IHousekeepingService : IDisposable { }

/// <summary>
/// 后台管家服务 —— 定时检查内存 + 清理过期数据
///
/// 2026-10-19 从 App 层迁入 Service 层：它是运维逻辑（GC 触发 + 数据保留策略），
/// 与 UI 无关，不该住在 WPF 入口项目里。阈值经 <see cref="HousekeepingOptions"/>
/// 显式传入，本类不认识 appsettings.json。
///
/// <para>
/// <b>它做三件事，各按自己的节拍</b>（默认值见 <see cref="HousekeepingOptions.Default"/>）：
/// <list type="number">
///   <item><b>查内存</b> —— 默认每 60 秒。工作集超过上限（默认 500MB）就强制 GC 两次并记一条前后对比。</item>
///   <item><b>删过期记录</b> —— 默认每 1 小时。截止点 = 当前时间往前 30 天（默认保留期），
///         交给仓储按记录创建时间（<c>CreatedAt</c>）删；按自然时间算，与班次无关。</item>
///   <item><b>删图片</b> —— 与第 2 项同一次调用里做（没有独立节拍）。
///         先按保留期（默认 30 天）删过期文件，再按容量上限（默认 1000MB）从最旧的开始删到限额以下。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>为什么这些清理必须在后台，而不是界面线程</b>：一次清理要扫目录、逐文件 stat/删、
/// 再对着库发一条 DELETE。几千张图时这是秒级甚至十秒级的同步 IO，
/// 放在 UI 线程上就是"每小时卡一下"，而卡的那一下恰好可能是操作员按急停/看判定的时候。
/// 界面线程只配做微秒级的事。
/// </para>
///
/// <para>
/// <b>归属</b>：DI 单例，构造即启动两个 <see cref="System.Timers.Timer"/>
/// （<c>AutoReset = true</c>），回调跑在<b>线程池</b>上 —— 不在 UI 线程，也不在启动线程。
/// 关停顺序由容器释放触发：<see cref="Dispose"/> 会先停 Timer，再最多等 5 秒让在跑的那一轮收尾
/// （此时可能正卡在 SQLite 写入上，直接拆会留下截断的库文件）。
/// <c>AppHost</c> 把它放在后台线程上释放，就是为了这最多 5 秒不冻界面。
/// </para>
///
/// <para>
/// <b>失败行为</b>：全吞。内存检查、清理、枚举目录、单文件删除，每一层都有 try/catch，
/// 失败只记日志并让下一轮重试 —— 管家是个后台保洁，它报错不能影响产线，
/// 也不能因为一个被 Verdict 占用的文件就从此不再清理。
/// </para>
///
/// <para>
/// <b>关键判断：重入保护用"跳过"而不是"排队"</b>。
/// <c>System.Timers.Timer</c> 在上一轮没跑完时会再起一个回调线程，若用信号量排队，
/// 结果是一轮比一轮慢、最后几十个线程同时抢库；跳过本轮只丢一次保洁，代价小得多。
/// </para>
/// </summary>
public class HousekeepingService : IHousekeepingService
{
    /// <summary>检测记录仓储。清理只调它的 <c>DeleteOlderThanAsync</c>，不做别的读写。</summary>
    private readonly IInspectionRepository _repo;

    /// <summary>
    /// 要清理的图片目录；<b>null = 不清理图片</b>（默认，也是生产部署应有的状态）。
    ///
    /// <para>
    /// 来自 <c>Storage:ImageFolder</c>（<c>AppPaths.StorageFolder</c> 的解析结果）。
    /// 未配置时为 null —— 图由 VisionMaster 存盘，且它按「输出图像」模块自带的数量/容量
    /// 上限自行清理，上位机不该再按目录扫一遍。
    /// </para>
    /// <para>
    /// <b>为什么默认不启用</b>（2026-10-27 F-31）：相机层删除后本程序不写图，
    /// 但这个服务仍在扫 <c>&lt;数据根&gt;\InspectedImages</c>，而那个目录永远是空的，
    /// 于是每小时打一条"图片清理完成，当前占用 0MB" —— 假成功比缺功能更糟：
    /// 真有一天盘满了，所有人都会拿这句话证明"清理在跑"，然后往错的方向查。
    /// </para>
    /// <para>
    /// <b>填了就是"指哪儿清哪儿"</b>：不做任何"猜平台目录"的动作，也不区分文件是谁写的。
    /// 所以不要把生产环境指向 VM 的输出目录 —— 那会让两个进程删同一批文件，
    /// 而且自检会往那个目录写 <c>.write_test</c> 探针。
    /// </para>
    /// </summary>
    private readonly string? _imageFolder;

    /// <summary>日志。可空：单测里不验证日志时不必造。</summary>
    private readonly ILogger<HousekeepingService>? _logger;

    /// <summary>内存检查定时器（默认 60 秒，AutoReset，回调在线程池上）。</summary>
    private readonly System.Timers.Timer _memTimer;

    /// <summary>数据库 + 图片清理定时器（默认 1 小时，AutoReset，回调在线程池上）。</summary>
    private readonly System.Timers.Timer _dbTimer;

    /// <summary>内存上限（MB）。超了就 GC —— 这是"最后手段"，不是内存管理策略。</summary>
    private readonly int _maxMemoryMb;

    /// <summary>检测记录保留天数（默认 30）。截止点按自然时间往前推，不是按班次。</summary>
    private readonly int _keepRecordsDays;

    /// <summary>图片保留天数（默认 30，构造时已校验 ≥ 记录保留天数，见 Options 的说明）。</summary>
    private readonly int _imageRetentionDays;

    /// <summary>图片目录容量上限（字节）。超了从最旧的删到限额以下。</summary>
    private readonly long _maxImageFolderBytes;

    /// <summary>清理重入保护：Timer.AutoReset 会在上一轮没跑完时再起一个线程</summary>
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);

    /// <summary>被重入保护跳过的轮数。只用于日志 —— 数字持续上涨说明"每轮都跑不完"，该调间隔了。</summary>
    private int _skippedRounds;

    /// <summary>
    /// 构造即启动 —— 没有 <c>Start()</c> 是有意的：少一个"忘了调"的失败面。
    ///
    /// 副作用：立刻创建并启动两个 Timer，第一次回调分别在 60 秒 / 1 小时后；
    /// 也就是说<b>解析这个单例就等于开始保洁</b>（<c>AppHost</c> 启动时显式取一次就是为此）。
    /// 线程：构造发生在 UI 线程（启动流程），但回调一律在线程池上，别在这里碰界面对象。
    /// 失败行为：阈值非法会在 <see cref="HousekeepingOptions"/> 构造时就抛（启动期失败），
    /// 走到这里时参数已经可信。
    /// </summary>
    /// <param name="imageFolder">
    /// 要代管清理的图片目录；<b>null = 不清理图片</b>（默认）。只有明确要上位机接管某个
    /// 图片目录时才传值 —— 生产上图由 VisionMaster 存、VisionMaster 删。
    /// </param>
    public HousekeepingService(IInspectionRepository repo,
        string? imageFolder,
        HousekeepingOptions options,
        ILogger<HousekeepingService>? logger = null)
    {
        _repo = repo;
        _imageFolder = imageFolder;
        _logger = logger;

        _maxMemoryMb = options.MaxMemoryMb;
        _keepRecordsDays = options.KeepRecordsDays;
        _imageRetentionDays = options.ImageRetentionDays;
        _maxImageFolderBytes = options.MaxImageFolderBytes;

        _memTimer = new System.Timers.Timer(options.MemoryCheckIntervalMs);
        _memTimer.Elapsed += (_, _) => CheckMemory();
        _memTimer.AutoReset = true;
        _memTimer.Start();

        _dbTimer = new System.Timers.Timer(options.DatabaseCleanIntervalMs);
        _dbTimer.Elapsed += (_, _) => CleanupDatabase();
        _dbTimer.AutoReset = true;
        _dbTimer.Start();

        // 图片清理是否启用必须打在启动日志第一行：
        // 没启用却只字不提，事后查"图为什么没被清"要从配置文件和代码两头猜。
        if (string.IsNullOrWhiteSpace(_imageFolder))
        {
            _logger?.LogInformation(
                "[管家] 已启动 - 内存检查:每{MemS}s 上限:{MemMax}MB | 数据库清理:每{DBS}s 保留:{KeepDays}天 | 图片清理:未启用（图由 VisionMaster 存删）",
                options.MemoryCheckIntervalMs / 1000, _maxMemoryMb,
                options.DatabaseCleanIntervalMs / 1000, _keepRecordsDays);
        }
        else
        {
            _logger?.LogInformation(
                "[管家] 已启动 - 内存检查:每{MemS}s 上限:{MemMax}MB | 数据库清理:每{DBS}s 保留:{KeepDays}天 | 图片清理:{Folder} 保留:{ImgDays}天 上限:{ImgMax}MB",
                options.MemoryCheckIntervalMs / 1000, _maxMemoryMb,
                options.DatabaseCleanIntervalMs / 1000, _keepRecordsDays,
                _imageFolder, _imageRetentionDays, _maxImageFolderBytes / 1024 / 1024);
        }
    }

    /// <summary>
    /// 内存超限 → 强制 GC。
    ///
    /// <para>
    /// 量的是工作集（WorkingSet64，即进程实际占的物理内存），不是托管堆大小 ——
    /// 操作员和产线关心的是"任务管理器里那个数"，托管堆小但进程占 1GB 一样要处理。
    /// </para>
    /// <para>
    /// 两次 Collect 中间夹一个 WaitForPendingFinalizers 是标准写法：
    /// 第一次回收后进终结器队列的对象要等终结器跑完才真正释放，
    /// 只调一次的话日志里的"释放了 XX MB"会明显偏小。
    /// <c>proc.Refresh()</c> 必须先调，否则读到的是缓存值 —— 前后一样，看起来像 GC 没用。
    /// </para>
    /// <para>
    /// 这是兜底手段不是内存管理策略：真出问题还是查泄漏。异常只记日志（本方法是后台保洁，
    /// 抛出去没人接，会直接带走线程池线程）。
    /// </para>
    /// </summary>
    private void CheckMemory()
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            long usedMb = proc.WorkingSet64 / 1024 / 1024;

            if (usedMb > _maxMemoryMb)
            {
                _logger?.LogWarning("[管家] 内存超限 {Used}MB > {Max}MB，触发 GC", usedMb, _maxMemoryMb);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                proc.Refresh();
                long afterMb = proc.WorkingSet64 / 1024 / 1024;
                _logger?.LogInformation("[管家] GC 后内存: {After}MB (释放约 {Freed}MB)",
                    afterMb, usedMb - afterMb);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[管家] 内存检查异常");
        }
    }

    /// <summary>
    /// 删过期数据库记录 + 过期图片文件（Timer 回调，async void 标准模式）。
    ///
    /// <c>async void</c> 平时是禁忌（异常会直接冒到线程池，把进程带走），这里能用是因为
    /// <see cref="CleanupOnceAsync"/> 内部把所有异常都吞成了 false + 日志 ——
    /// 换句话说"这个 async void 不会抛"是被下一层保证的，改那个方法时别把 catch 拆了。
    /// </summary>
    private async void CleanupDatabase()
    {
        await CleanupOnceAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 执行一轮清理。抽成独立方法是为了可测——原实现只能靠等 1 小时 Timer
    /// 或反射才能触发，等于不可验证。
    ///
    /// 重入保护：拿不到 gate 就跳过本轮并计数，绝不排队——
    /// 排队的后果是「一轮慢，后面全部堆起来」，最后同时跑几十轮。
    /// </summary>
    /// <returns>true = 本轮真正执行；false = 上一轮未结束被跳过（异常已记日志）</returns>
    internal async Task<bool> CleanupOnceAsync()
    {
        if (!_cleanupGate.Wait(0))
        {
            int skipped = Interlocked.Increment(ref _skippedRounds);
            _logger?.LogWarning("[管家] 上一轮清理尚未结束，跳过本轮（累计跳过 {Skipped} 次）", skipped);
            return false;
        }

        try
        {
            // ── 数据库 ──
            var cutoff = DateTime.Now.AddDays(-_keepRecordsDays);
            var deleted = await _repo.DeleteOlderThanAsync(cutoff).ConfigureAwait(false);
            if (deleted > 0)
                _logger?.LogInformation("[管家] 数据库清理完成，删除 {Count} 条记录", deleted);

            // ── 图片文件夹 ──
            CleanupImages();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[管家] 数据库清理异常");
            return false;
        }
        finally
        {
            _cleanupGate.Release();
        }
    }

    /// <summary>
    /// 删过期图片 + 超出容量上限时删最旧的。**只在 <c>Storage:ImageFolder</c> 配了才跑**，
    /// 默认不配 → 直接返回，一条日志都不打（不打"清理完成 0MB"那种假成功日志）。
    ///
    /// 三条约束：
    ///   1) 两遍扫描：先删过期，只对「活着的文件」统计容量——
    ///      原实现把已删文件的大小也算进 totalSize，日志报的占用是虚高的。
    ///   2) 逐个文件 try：一个文件删不掉（被 Verdict 软件占用/只读）不得中止整轮，
    ///      否则排在它后面的过期图片永远清不掉，目录会一直涨。
    ///   3) EnumerateFiles 流式枚举，不再把整目录一次性拉进内存。
    /// </summary>
    private void CleanupImages()
    {
        // 取到局部变量再判空：字段的可空状态不会被编译器跟踪，
        // 用局部变量才能让后面两处 Directory.* 调用干净地通过可空分析。
        var folder = _imageFolder;

        // 未配置 = 上位机不管图片。这里必须静默返回：
        // 打一条"图片清理完成"就变成假成功（看起来在管、实际什么都没管）。
        if (string.IsNullOrWhiteSpace(folder)) return;

        if (!Directory.Exists(folder)) return;

        var cutoff = DateTime.Now.AddDays(-_imageRetentionDays);
        var live = new List<(string Path, DateTime LastWrite, long Size)>();
        long totalSize = 0;
        int expired = 0, failed = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".write_test") || file.EndsWith(".db_write_test")) continue;

                try
                {
                    var fi = new FileInfo(file);
                    if (!fi.Exists) continue;                       // 枚举后被别人删了

                    if (fi.LastWriteTime < cutoff)
                    {
                        fi.Delete();
                        expired++;
                        continue;
                    }

                    live.Add((file, fi.LastWriteTime, fi.Length));
                    totalSize += fi.Length;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger?.LogWarning(ex, "[管家] 文件处理失败，已跳过: {File}", Path.GetFileName(file));
                }
            }
        }
        catch (Exception ex)
        {
            // 目录级失败（目录被删/无权限）：本轮放弃，下一轮重试
            _logger?.LogError(ex, "[管家] 图片目录枚举失败");
            return;
        }

        if (expired > 0 || failed > 0)
            _logger?.LogInformation("[管家] 过期图片删除 {Expired} 张，跳过 {Failed} 张", expired, failed);

        // 超过容量上限 → 按时间从旧到新删（只从活着的文件里挑）
        if (totalSize > _maxImageFolderBytes)
        {
            live.Sort((a, b) => a.LastWrite.CompareTo(b.LastWrite));
            foreach (var (path, _, size) in live)
            {
                if (totalSize <= _maxImageFolderBytes) break;
                try
                {
                    File.Delete(path);
                    totalSize -= size;
                    _logger?.LogDebug("[管家] 超容删除图片: {File}", Path.GetFileName(path));
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[管家] 超容删除失败，已跳过: {File}", Path.GetFileName(path));
                }
            }
        }

        _logger?.LogInformation("[管家] 图片清理完成，当前占用 {Size}MB", totalSize / 1024 / 1024);
    }

    /// <summary>
    /// 1 = 已释放。用 Interlocked 而不是 bool：容器释放与本类自己的 Dispose
    /// 可能在不同线程上各来一次，重复 Dispose 会把已释放的 SemaphoreSlim 再拆一遍（抛异常）。
    /// </summary>
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _memTimer?.Stop(); _memTimer?.Dispose();
        _dbTimer?.Stop(); _dbTimer?.Dispose();

        // 停 Timer 只是不再起新一轮，正在跑的那一轮还在写数据库。
        // 关机瞬间若正好在 SaveChanges，SQLite 文件可能被截断。这里最多等 5 秒收尾。
        try
        {
            if (_cleanupGate.Wait(5000))
                _cleanupGate.Release();
            else
                _logger?.LogWarning("[管家] 关闭时仍有清理轮次未结束（等待超时 5s）");
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略
        }

        _cleanupGate.Dispose();
    }
}
