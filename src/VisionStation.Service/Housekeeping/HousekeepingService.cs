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

    /// <summary>默认值（与 appsettings.json 中的 Housekeeping 段一致）</summary>
    public static HousekeepingOptions Default => new(
        500, 30, 30, 1000L * 1024 * 1024, 60_000, 3_600_000);
}

public interface IHousekeepingService : IDisposable { }

/// <summary>
/// 后台管家服务 —— 定时检查内存 + 清理过期数据
///
/// 2026-10-19 从 App 层迁入 Service 层：它是运维逻辑（GC 触发 + 数据保留策略），
/// 与 UI 无关，不该住在 WPF 入口项目里。阈值经 <see cref="HousekeepingOptions"/>
/// 显式传入，本类不认识 appsettings.json。
/// </summary>
public class HousekeepingService : IHousekeepingService
{
    private readonly IInspectionRepository _repo;
    private readonly string _imageFolder;
    private readonly ILogger<HousekeepingService>? _logger;
    private readonly System.Timers.Timer _memTimer;
    private readonly System.Timers.Timer _dbTimer;
    private readonly int _maxMemoryMb;
    private readonly int _keepRecordsDays;
    private readonly int _imageRetentionDays;
    private readonly long _maxImageFolderBytes;

    /// <summary>清理重入保护：Timer.AutoReset 会在上一轮没跑完时再起一个线程</summary>
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private int _skippedRounds;

    public HousekeepingService(IInspectionRepository repo,
        string imageFolder,
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

        _logger?.LogInformation(
            "[管家] 已启动 - 内存检查:每{MemS}s 上限:{MemMax}MB | 数据库清理:每{DBS}s 保留:{KeepDays}天 | 图片保留:{ImgDays}天 上限:{ImgMax}MB",
            options.MemoryCheckIntervalMs / 1000, _maxMemoryMb,
            options.DatabaseCleanIntervalMs / 1000, _keepRecordsDays,
            _imageRetentionDays, _maxImageFolderBytes / 1024 / 1024);
    }

    /// <summary>内存超限 → 强制 GC</summary>
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

    /// <summary>删过期数据库记录 + 过期图片文件（Timer 回调，async void 标准模式）</summary>
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
    /// 删过期图片 + 超出容量上限时删最旧的。
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
        if (!Directory.Exists(_imageFolder)) return;

        var cutoff = DateTime.Now.AddDays(-_imageRetentionDays);
        var live = new List<(string Path, DateTime LastWrite, long Size)>();
        long totalSize = 0;
        int expired = 0, failed = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(_imageFolder, "*.*", SearchOption.AllDirectories))
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
