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
    /// <returns>保存的文件路径，未保存时返回 null</returns>
    public string? Save(CameraFrame frame, InspectionReport report)
    {
        if (!ShouldSave(report))
            return null;

        // 按日期分文件夹
        var dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
        var folder = Path.Combine(_baseFolder, dateFolder);
        Directory.CreateDirectory(folder);

        // 文件名：时间戳_判定_原文件名
        var baseName = Path.GetFileNameWithoutExtension(frame.FilePath);
        if (string.IsNullOrEmpty(baseName)) baseName = "capture";

        var fileName = $"{DateTime.Now:HHmmssfff}_{report.OverallVerdict}_{baseName}.png";
        var fullPath = Path.Combine(folder, fileName);

        // 写入磁盘
        Cv2.ImWrite(fullPath, frame.Image);
        return fullPath;
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
