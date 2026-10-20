using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionStation.Core.Models;

namespace VisionStation.Devices;

/// <summary>
/// USB/笔记本摄像头 — 基于 OpenCV VideoCapture，实现 ICamera
///
/// 可用性判据：连续读帧失败 FailureThreshold 次即判离线（拔线/被独占）。
/// 只置真不置假的 IsAvailable 会让界面在摄像头已拔掉之后仍显示"就绪"，
/// 而 Capture() 永远返回 null —— 检查者自身失效，产线停了却没有任何告警。
/// </summary>
public class WebcamCamera : ICamera
{
    private readonly int _deviceIndex;
    private readonly ILogger<WebcamCamera>? _logger;

    private VideoCapture? _capture;
    private bool _opened;
    private int _consecutiveFailures;

    /// <summary>连续读帧失败多少次判定离线</summary>
    public int FailureThreshold { get; set; } = 3;

    public bool IsAvailable => _opened && _consecutiveFailures < FailureThreshold;
    public int FrameCount { get; private set; }

    /// <param name="deviceIndex">摄像头索引，0=默认摄像头</param>
    /// <remarks>
    /// 只赋值，不打开设备（W-01/W-02 / S2.5）。构造即连接会让"造一个候选实例"
    /// 变成独占摄像头，热切换时新旧实例抢同一个设备必然失败。
    /// </remarks>
    public WebcamCamera(int deviceIndex = 0, ILogger<WebcamCamera>? logger = null)
    {
        _deviceIndex = deviceIndex;
        _logger = logger;
    }

    /// <summary>打开（或重新打开）摄像头 —— close() 之后可再次调用</summary>
    public void open()
    {
        if (_opened) return;
        OpenDevice();
    }

    private void OpenDevice()
    {
        try
        {
            _capture = new VideoCapture(_deviceIndex);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Webcam] 打开摄像头异常 index={Index}", _deviceIndex);
            _opened = false;
            return;
        }

        if (!_capture.IsOpened())
        {
            _logger?.LogWarning("[Webcam] 无法打开摄像头 index={Index}", _deviceIndex);
            _capture.Dispose();
            _capture = null;
            _opened = false;
            return;
        }

        _opened = true;
        _consecutiveFailures = 0;
        _logger?.LogInformation("[Webcam] 摄像头已就绪 index={Index}", _deviceIndex);
    }

    public CameraFrame? Capture()
    {
        if (!_opened || _capture == null) return null;

        using var mat = new Mat();
        if (!_capture.Read(mat) || mat.Empty())
        {
            _consecutiveFailures++;
            if (_consecutiveFailures == FailureThreshold)
                _logger?.LogError("[Webcam] 连续 {N} 次读帧失败，判定离线 index={Index}",
                    _consecutiveFailures, _deviceIndex);
            return null;
        }

        _consecutiveFailures = 0;
        FrameCount++;
        return new CameraFrame
        {
            Image = mat.Clone(),
            Timestamp = DateTime.Now,
            FilePath = $"Webcam_Frame_{FrameCount}"
        };
    }

    public void close()
    {
        _capture?.Dispose();
        _capture = null;
        _opened = false;
    }

    public void Dispose() => close();
}
