using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
    private readonly IConfiguration _config;
    private readonly AppPaths _paths;
    private readonly ILoggerFactory? _loggerFactory;

    public CameraFactory(AppPaths paths, IConfiguration config, ILoggerFactory? loggerFactory = null)
    {
        _paths = paths;
        _config = config;
        _loggerFactory = loggerFactory;
    }

    /// <summary>根据配置文件的 Mode 创建相机</summary>
    public ICamera Create()
    {
        return CreateByMode(_config["Camera:Mode"] ?? "File");
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
                _config["Camera:FileFilter"] ?? "*.jpg;*.png;*.bmp",
                bool.Parse(_config["Camera:Loop"] ?? "true"),
                _loggerFactory?.CreateLogger<FileCamera>())
        };

        // 将来：
        // "Basler" => new BaslerCamera(...)
        // "MindVision" => new MindVisionCamera(...)
        // "Remote" => new RemoteCamera(mqttBroker)
    }
}
