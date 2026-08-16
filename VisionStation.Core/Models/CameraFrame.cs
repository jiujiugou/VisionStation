using OpenCvSharp;

namespace VisionStation.Core.Models;

/// <summary>
/// 相机采集帧：Mat 图像 + 时间戳
/// </summary>
public class CameraFrame
{
    /// <summary>OpenCV 图像数据</summary>
    public Mat Image { get; init; } = new();

    /// <summary>采集时间戳</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>图片文件路径（FileCamera 模式下有意义）</summary>
    public string FilePath { get; init; } = string.Empty;
}
