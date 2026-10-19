using OpenCvSharp;
using VisionStation.Core.Models;

namespace VisionStation.Storage;

/// <summary>
/// 图片存储模式
///   All      — 保存每一帧（开发调试用）
///   NGOnly   — 只保存不合格的帧（生产环境常用，省空间）
///   Sampling — 每 N 张 OK 存一张（质量抽检用）
/// </summary>
public enum ImageStoreMode { All, NGOnly, Sampling }

/// <summary>
/// 存图结果的三态。
///
/// 为什么不能只返回 string?（原来的写法）：
///   "按策略不存"（Skipped）和"想存但存失败了"（Failed）都表现为 null，
///   调用方无法区分，等于把故障和正常策略混成一个返回值 —— 故障被静默吞掉。
/// </summary>
public enum ImageSaveOutcome
{
    /// <summary>按存储策略决定不保存（正常行为）</summary>
    Skipped,

    /// <summary>已落盘</summary>
    Saved,

    /// <summary>本应保存但失败（磁盘满 / 无权限 / 路径非法 / 编码失败）</summary>
    Failed
}

/// <summary>一次存图操作的完整结果</summary>
public sealed record ImageSaveResult(ImageSaveOutcome Outcome, string? Path, string? Error)
{
    public bool IsSaved => Outcome == ImageSaveOutcome.Saved;
    public bool IsFailed => Outcome == ImageSaveOutcome.Failed;

    public static readonly ImageSaveResult Skipped = new(ImageSaveOutcome.Skipped, null, null);
}

/// <summary>
/// 图片文件管理服务 —— 负责检测图像的持久化存储
///
/// 策略：
///   1. 按日期分文件夹（yyyy-MM-dd）
///   2. 文件名格式：时间戳_判定_原文件名.png
///   3. 三种保存模式可配置切换
///
/// 为什么需要这个服务？
///   产线上每天可能有几万次检测。如果全存图片，硬盘一周就爆。
///   不同阶段需要不同策略：开发全存、生产存 NG、质量抽检按比例。
/// </summary>
public class ImageStoreService
{
    private readonly string _baseFolder;
    private ImageStoreMode _mode = ImageStoreMode.All;
    private int _samplingRate = 10;   // 抽样比例：每 N 张 OK 存一张
    private int _okCounter;           // OK 计数器

    public ImageStoreService(string baseFolder)
    {
        _baseFolder = baseFolder;
        Directory.CreateDirectory(baseFolder);
    }

    /// <summary>设置存储模式和抽样比例</summary>
    public void SetMode(ImageStoreMode mode, int samplingRate = 10)
    {
        _mode = mode;
        _samplingRate = samplingRate;
    }

    /// <summary>保存检测图像（根据模式判断是否保存）</summary>
    /// <param name="frame">采集帧</param>
    /// <param name="report">检测报告</param>
    /// <returns>
    /// 三态结果。失败时**不抛异常**，而是返回 Failed + 原因 ——
    /// 存图失败不该让整帧检测结果作废（结果本身仍有价值），
    /// 但调用方必须看得见，否则 NG 图会在无人知晓的情况下永久丢失。
    /// </returns>
    public ImageSaveResult Save(CameraFrame frame, InspectionReport report)
    {
        if (!ShouldSave(report))
            return ImageSaveResult.Skipped;

        string? fullPath = null;
        try
        {
            // 按日期分文件夹
            var dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
            var folder = Path.Combine(_baseFolder, dateFolder);
            Directory.CreateDirectory(folder);

            // 文件名：时间戳_判定_原文件名
            var baseName = Path.GetFileNameWithoutExtension(frame.FilePath);
            if (string.IsNullOrEmpty(baseName)) baseName = "capture";

            var fileName = $"{DateTime.Now:HHmmssfff}_{report.OverallVerdict}_{baseName}.png";
            fullPath = Path.Combine(folder, fileName);

            // 写入磁盘。
            // 注意：Cv2.ImWrite 失败时**只返回 false，不抛异常** ——
            // 这是最容易踩的坑：不看返回值就等于认定"存成功了"。
            if (!Cv2.ImWrite(fullPath, frame.Image))
                return new ImageSaveResult(ImageSaveOutcome.Failed, null,
                    $"ImWrite 返回 false（磁盘满？路径不可写？）：{fullPath}");

            return new ImageSaveResult(ImageSaveOutcome.Saved, fullPath, null);
        }
        catch (Exception ex)
        {
            return new ImageSaveResult(ImageSaveOutcome.Failed, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>根据当前模式判断是否保存本帧</summary>
    private bool ShouldSave(InspectionReport report)
    {
        return _mode switch
        {
            ImageStoreMode.All => true,
            ImageStoreMode.NGOnly => !report.IsOk,
            ImageStoreMode.Sampling => report.IsOk ? (++_okCounter % _samplingRate == 0) : true,
            _ => true
        };
    }
}
