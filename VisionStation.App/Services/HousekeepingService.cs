using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VisionStation.Storage;

namespace VisionStation.App.Services;

/// <summary>
/// 后台管家服务 —— 定时检查内存 + 清理过期数据
/// 参数从 appsettings.json 读取，不硬编码
/// </summary>
public interface IHousekeepingService : IDisposable { }

public class HousekeepingService : IHousekeepingService
{
    private readonly InspectionRepository _repo;
    private readonly string _imageFolder;
    private readonly ILogger<HousekeepingService>? _logger;
    private readonly System.Timers.Timer _memTimer;
    private readonly System.Timers.Timer _dbTimer;
    private readonly int _maxMemoryMb;
    private readonly int _keepRecordsDays;
    private readonly int _imageRetentionDays;
    private readonly long _maxImageFolderBytes;

    public HousekeepingService(InspectionRepository repo,
        string imageFolder,
        IConfiguration config,
        ILogger<HousekeepingService>? logger = null)
    {
        _repo = repo;
        _imageFolder = imageFolder;
        _logger = logger;

        // ── 从 JSON 读取阈值，不在代码里硬编码 ──
        _maxMemoryMb = int.Parse(config["Housekeeping:MaxMemoryMB"] ?? "500");
        _keepRecordsDays = int.Parse(config["Housekeeping:KeepRecordsDays"] ?? "30");
        _imageRetentionDays = int.Parse(config["Housekeeping:ImageRetentionDays"] ?? "7");
        _maxImageFolderBytes = long.Parse(config["Housekeeping:MaxImageFolderMB"] ?? "1000") * 1024 * 1024;

        var memInterval = int.Parse(config["Housekeeping:MemoryCheckIntervalMs"] ?? "60000");
        var dbInterval = int.Parse(config["Housekeeping:DatabaseCleanIntervalMs"] ?? "3600000");

        _memTimer = new System.Timers.Timer(memInterval);
        _memTimer.Elapsed += (_, _) => CheckMemory();
        _memTimer.AutoReset = true;
        _memTimer.Start();

        _dbTimer = new System.Timers.Timer(dbInterval);
        _dbTimer.Elapsed += (_, _) => CleanupDatabase();
        _dbTimer.AutoReset = true;
        _dbTimer.Start();

        _logger?.LogInformation(
            "[管家] 已启动 - 内存检查:每{MemS}s 上限:{MemMax}MB | 数据库清理:每{DBS}s 保留:{KeepDays}天 | 图片保留:{ImgDays}天 上限:{ImgMax}MB",
            memInterval / 1000, _maxMemoryMb,
            dbInterval / 1000, _keepRecordsDays,
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
        try
        {
            // ── 数据库 ──
            var cutoff = DateTime.Now.AddDays(-_keepRecordsDays);
            var deleted = await _repo.DeleteOlderThanAsync(cutoff);
            if (deleted > 0)
                _logger?.LogInformation("[管家] 数据库清理完成，删除 {Count} 条记录", deleted);

            // ── 图片文件夹 ──
            CleanupImages();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[管家] 数据库清理异常");
        }
    }

    /// <summary>删过期图片 + 超出容量上限时删最旧的</summary>
    private void CleanupImages()
    {
        if (!Directory.Exists(_imageFolder)) return;

        try
        {
            var cutoff = DateTime.Now.AddDays(-_imageRetentionDays);
            long totalSize = 0;
            var files = new List<(string Path, DateTime LastWrite, long Size)>();

            foreach (var file in Directory.GetFiles(_imageFolder, "*.*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".write_test")) continue;

                var fi = new FileInfo(file);
                totalSize += fi.Length;
                files.Add((file, fi.LastWriteTime, fi.Length));

                // 过期直接删
                if (fi.LastWriteTime < cutoff)
                {
                    File.Delete(file);
                    _logger?.LogDebug("[管家] 删除过期图片: {File}", Path.GetFileName(file));
                }
            }

            // 超过容量上限 → 按时间从旧到新删
            if (totalSize > _maxImageFolderBytes)
            {
                files.Sort((a, b) => a.LastWrite.CompareTo(b.LastWrite));
                foreach (var (path, _, size) in files)
                {
                    if (totalSize <= _maxImageFolderBytes) break;
                    File.Delete(path);
                    totalSize -= size;
                    _logger?.LogDebug("[管家] 超容删除图片: {File}", Path.GetFileName(path));
                }
                _logger?.LogInformation("[管家] 图片清理完成，当前占用 {Size}MB",
                    totalSize / 1024 / 1024);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[管家] 图片清理异常");
        }
    }

    public void Dispose()
    {
        _memTimer?.Stop(); _memTimer?.Dispose();
        _dbTimer?.Stop(); _dbTimer?.Dispose();
    }
}
