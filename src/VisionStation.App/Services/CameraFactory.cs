using Microsoft.Extensions.Logging;
using VisionStation.App.Composition;
using VisionStation.Devices;

namespace VisionStation.App.Services;

/// <summary>
/// 相机工厂 —— Mode 字符串 → ICamera 实例
///
/// 职责：把所有相机创建逻辑（new WebcamCamera / new FileCamera ...）封在这里。
/// App.xaml.cs 和 SwappableCamera 都不需要知道每种相机怎么 new。
/// </summary>
public class CameraFactory
{
    private readonly CameraOptions _camera;
    private readonly AppPaths _paths;
    private readonly ILoggerFactory? _loggerFactory;

    public CameraFactory(AppPaths paths, CameraOptions camera, ILoggerFactory? loggerFactory = null)
    {
        _paths = paths;
        _camera = camera;
        _loggerFactory = loggerFactory;
    }

    /// <summary>根据 Options 的 Mode 创建相机（S5 后 Mode 只在这一处消费 Options）</summary>
    public ICamera Create()
    {
        return CreateByMode(_camera.Mode);
    }

    /// <summary>根据模式字符串创建相机。加新相机类型只改这里。</summary>
    public ICamera CreateByMode(string mode)
    {
        return mode switch
        {
            "Webcam" => new WebcamCamera(0,
                _loggerFactory?.CreateLogger<WebcamCamera>()),
            "Hikvision" => new HikCamera(
                _loggerFactory?.CreateLogger<HikCamera>()),
            _ => new FileCamera(
                _paths.ImageFolder,
                _camera.FileFilter,
                _camera.Loop,
                _loggerFactory?.CreateLogger<FileCamera>())
        };

        // 将来：
        // "Basler" => new BaslerCamera(...)
        // "MindVision" => new MindVisionCamera(...)
        // "Remote" => new RemoteCamera(mqttBroker)
    }
}
