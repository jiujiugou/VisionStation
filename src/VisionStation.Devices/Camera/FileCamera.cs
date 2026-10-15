using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionStation.Core.Models;

namespace VisionStation.Devices;

/// <summary>
/// 文件相机 — 从本地文件夹按顺序读取图片，模拟工业相机采图
/// </summary>
public class FileCamera : ICamera
{
    private readonly string _folderPath;
    private readonly string _fileFilter;
    private readonly List<string> _files = new();
    private int _index;
    private readonly bool _loop;
    private readonly ILogger<FileCamera>? _logger;

    public bool IsAvailable => _files.Count > 0;
    public int FrameCount { get; private set; }
    public int TotalFiles => _files.Count;

    public void open() => RefreshFileList();
    public void close() { }

    public FileCamera(string folderPath, string fileFilter = "*.jpg", bool loop = true,
        ILogger<FileCamera>? logger = null)
    {
        _folderPath = folderPath;
        _fileFilter = fileFilter;
        _loop = loop;
        _logger = logger;

        RefreshFileList();
    }

    public void RefreshFileList()
    {
        _files.Clear();
        _index = 0;

        if (!Directory.Exists(_folderPath))
        {
            _logger?.LogWarning("[FileCamera] 文件夹不存在: {Path}", _folderPath);
            return;
        }

        var extensions = _fileFilter.Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                     .Select(f => f.Trim().TrimStart('*'))
                                     .ToArray();

        _logger?.LogInformation("[FileCamera] 过滤扩展名: [{Exts}]", string.Join(", ", extensions));
        
            if (extensions.Length == 0)
            extensions = new[] { ".jpg", ".png", ".bmp" };

        foreach (var file in Directory.GetFiles(_folderPath))
        {
            var ext = Path.GetExtension(file).ToLower();
            if (extensions.Contains(ext))
                _files.Add(file);
        }

        _files.Sort();

        _logger?.LogInformation("[FileCamera] 刷新完成 - 路径:{Path} 找到:{Count}张图 过滤:{Filter}",
            _folderPath, _files.Count, string.Join(",", extensions));

        if (_files.Count == 0)
            _logger?.LogWarning("[FileCamera] 警告: 未找到匹配图片! 目录中的文件: {Files}",
                string.Join(", ", Directory.GetFiles(_folderPath).Select(Path.GetFileName)));
    }

    public CameraFrame? Capture()
    {
        if (_files.Count == 0)
        {
            _logger?.LogWarning("[FileCamera] 无图片可采集");
            return null;
        }

        if (_index >= _files.Count)
        {
            if (_loop)
            {
                _logger?.LogInformation("[FileCamera] 已到末尾, 循环回到第1张 (共{Count}张)", _files.Count);
                _index = 0;
            }
            else
            {
                _logger?.LogInformation("[FileCamera] 已到末尾, 非循环模式停止 (共{Count}张)", _files.Count);
                return null;
            }
        }

        var filePath = _files[_index];
        _logger?.LogDebug("[FileCamera] 读取第{Idx}/{Total}张: {File}",
            _index + 1, _files.Count, Path.GetFileName(filePath));

        var image = Cv2.ImRead(filePath, ImreadModes.Color);
        if (image.Empty())
        {
            _logger?.LogError("[FileCamera] 读取失败,图片为空: {File}", filePath);
            _index++;
            return null;
        }

        FrameCount++;
        _index++;

        return new CameraFrame
        {
            Image = image,
            Timestamp = DateTime.Now,
            FilePath = filePath
        };
    }
}
