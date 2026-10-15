using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionStation.Core.Models;

namespace VisionStation.Devices;

/// <summary>
/// USB/笔记本摄像头 — 基于 OpenCV VideoCapture，实现 ICamera
/// </summary>
public class WebcamCamera : ICamera, IDisposable
{
    private readonly VideoCapture _capture;
    private readonly ILogger<WebcamCamera>? _logger;

    public bool IsAvailable { get; }
    public int FrameCount { get; private set; }

    /// <param name="deviceIndex">摄像头索引，0=默认摄像头</param>
    public WebcamCamera(int deviceIndex = 0, ILogger<WebcamCamera>? logger = null)
    {
        _logger = logger;
        _capture = new VideoCapture(deviceIndex);

        if (!_capture.IsOpened())
        {
            _logger?.LogWarning("[Webcam] 无法打开摄像头 index={Index}", deviceIndex);
            IsAvailable = false;
            _capture.Dispose();
            return;
        }

        IsAvailable = true;
        _logger?.LogInformation("[Webcam] 摄像头已就绪 index={Index}", deviceIndex);
    }

    public CameraFrame? Capture()
    {
        if (!IsAvailable) return null;

        using var mat = new Mat();
        if (!_capture.Read(mat) || mat.Empty())
            return null;

        FrameCount++;
        return new CameraFrame
        {
            Image = mat.Clone(),
            Timestamp = DateTime.Now,
            FilePath = $"Webcam_Frame_{FrameCount}"
        };
    }

    public void open() { /* 构造函数已打开 */ }
    public void close() => Dispose();
    public void Dispose() => _capture?.Dispose();
}
