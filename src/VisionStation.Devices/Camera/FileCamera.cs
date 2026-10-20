using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionStation.Core.Models;

namespace VisionStation.Devices;

/// <summary>
/// 文件相机 — 从本地文件夹按顺序读取图片，模拟工业相机采图
///
/// 并发约定：刷新与采集可能来自不同线程（UI 点"重新连接" vs 检测线程取图），
/// 因此文件列表不做原地 Clear/Add，而是**整个换引用**：
/// 采集线程每次先取一份快照，手里的列表永远不会被另一个线程改掉。
/// </summary>
public sealed class FileCamera : ICamera
{
    private readonly string _folderPath;
    private readonly string _fileFilter;
    private readonly bool _loop;
    private readonly ILogger<FileCamera>? _logger;

    private volatile List<string> _files = new();   // 只整体替换，不原地修改
    private int _index;

    /// <summary>过滤器分隔符（"*.jpg|*.png" 这种写法），提成静态字段避免每次刷新都分配数组</summary>
    private static readonly char[] FilterSeparators = { '|', ';' };

    /// <summary>还有没有图可采（非循环模式读完后为 false，不能只看文件总数）</summary>
    public bool IsAvailable
    {
        get
        {
            var files = _files;
            return files.Count > 0 && (_loop || Volatile.Read(ref _index) < files.Count);
        }
    }

    public int FrameCount { get; private set; }
    public int TotalFiles => _files.Count;

    /// <summary>本实现只持有托管对象（文件路径列表），关闭无需动作。</summary>
    public void close() { }

    /// <summary>
    /// 空释放：FileCamera 没有句柄、流或线程，内存交给 GC。
    /// 之所以仍然显式实现，是因为 ICamera 继承 IDisposable 是为了强制
    /// 真正持有 SDK 句柄的实现（HikCamera）无法忘记释放；
    /// 这里什么都不做是正确结果，不是遗漏。
    /// </summary>
    public void Dispose()
    {
        close();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 只赋值，不扫目录（W-01/W-02 / S2.5）。
    /// 构造函数有副作用 = "new 一个对象"就会碰磁盘：DI 装配、热切换的候选实例、
    /// 单元测试都会顺手触发一次真实 IO，而且失败路径只能靠 constructor 里 catch 兜住。
    /// 连接语义统一交给 open()。
    /// </summary>
    public FileCamera(string folderPath, string fileFilter = "*.jpg", bool loop = true,
        ILogger<FileCamera>? logger = null)
    {
        _folderPath = folderPath;
        _fileFilter = fileFilter;
        _loop = loop;
        _logger = logger;
    }

    /// <summary>连接 = 扫一次目录。未 open 时文件列表为空，IsAvailable 必为 false。</summary>
    public void open() => RefreshFileList();

    public void RefreshFileList()
    {
        var list = new List<string>();

        try
        {
            if (!Directory.Exists(_folderPath))
            {
                _logger?.LogWarning("[FileCamera] 文件夹不存在: {Path}", _folderPath);
                Publish(list);
                return;
            }

            var extensions = _fileFilter.Split(FilterSeparators, StringSplitOptions.RemoveEmptyEntries)
                                         .Select(f => f.Trim().TrimStart('*'))
                                         .ToArray();

            _logger?.LogInformation("[FileCamera] 过滤扩展名: [{Exts}]", string.Join(", ", extensions));

            if (extensions.Length == 0)
                extensions = new[] { ".jpg", ".png", ".bmp" };

            // 目录存在 ≠ 可读：权限、被占用、路径过长都会在这里抛。
            // open() 会调用本方法，异常若逃出去 = 启动失败（或热切换候选构造失败），
            // 必须就地降级为空列表。
            var all = Directory.GetFiles(_folderPath);

            foreach (var file in all)
            {
                // ToLowerInvariant：扩展名比较与系统区域无关，避免土耳其语 I→ı 这类坑
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (extensions.Contains(ext))
                    list.Add(file);
            }

            list.Sort();

            _logger?.LogInformation("[FileCamera] 刷新完成 - 路径:{Path} 找到:{Count}张图 过滤:{Filter}",
                _folderPath, list.Count, string.Join(",", extensions));

            if (list.Count == 0)
                _logger?.LogWarning("[FileCamera] 警告: 未找到匹配图片! 目录中的文件: {Files}",
                    string.Join(", ", all.Select(Path.GetFileName)));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[FileCamera] 扫描目录失败，按空列表继续运行: {Path}", _folderPath);
            list.Clear();
        }

        Publish(list);
    }

    /// <summary>原子发布新列表：先归零下标，再换引用（读者手里的旧快照不受影响）</summary>
    private void Publish(List<string> list)
    {
        Volatile.Write(ref _index, 0);
        _files = list;
    }

    public CameraFrame? Capture()
    {
        var files = _files;      // 快照：刷新会整体换引用，这份永远完整
        if (files.Count == 0)
        {
            _logger?.LogWarning("[FileCamera] 无图片可采集");
            return null;
        }

        int index = Volatile.Read(ref _index);
        if (index >= files.Count)
        {
            if (_loop)
            {
                _logger?.LogInformation("[FileCamera] 已到末尾, 循环回到第1张 (共{Count}张)", files.Count);
                index = 0;
            }
            else
            {
                _logger?.LogInformation("[FileCamera] 已到末尾, 非循环模式停止 (共{Count}张)", files.Count);
                return null;
            }
        }

        var filePath = files[index];
        _logger?.LogDebug("[FileCamera] 读取第{Idx}/{Total}张: {File}",
            index + 1, files.Count, Path.GetFileName(filePath));

        var image = Cv2.ImRead(filePath, ImreadModes.Color);
        Volatile.Write(ref _index, index + 1);

        if (image.Empty())
        {
            _logger?.LogError("[FileCamera] 读取失败,图片为空: {File}", filePath);
            image.Dispose();
            return null;
        }

        FrameCount++;
        return new CameraFrame
        {
            Image = image,
            Timestamp = DateTime.Now,
            FilePath = filePath
        };
    }
}
