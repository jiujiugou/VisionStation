using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VisionStation.App.Services.Imaging;

/// <summary>
/// 图像来源设置 —— 一个可以在界面上随时改、改完立即生效、落盘后重启仍然有效的设置。
///
/// 为什么需要它：VM 发来的那段"图像路径"经常不完整（只发目录、或发相对路径、
/// 或者干脆没配存图），而每换一次现场条件就改一次代码、重编译一次，
/// 这件事在调试期占的时间比写功能还多。设置放磁盘，现场自己改。
///
/// 它**不替代**报文里的 imagePath：报文里有完整路径时优先用报文的，
/// 这里只是"报文没给出可用文件时"的兜底来源。
/// </summary>
public sealed record ImageSourceSettings
{
    /// <summary>是否启用目录兜底（关掉时行为与以前完全一致）</summary>
    public bool FolderFallbackEnabled { get; init; }

    /// <summary>兜底目录。VM 会按 年月\日期 分层，所以填最上层即可</summary>
    public string Folder { get; init; } = "";

    /// <summary>
    /// 是否递归子目录。VM「输出图像」默认生成 <c>202610\20261026\xxx.bmp</c>，
    /// 不勾这一项在真实目录结构下一张也找不到 —— 所以默认开。
    /// </summary>
    public bool Recursive { get; init; } = true;

    /// <summary>未启用时的空设置</summary>
    public static ImageSourceSettings Disabled { get; } = new();
}

/// <summary>
/// 设置的读写。位置由 <c>AppPaths.ImageSourceSettingsPath</c> 决定（数据根下，不在源码目录）。
///
/// 线程说明：读发生在检测线程（每帧解析图像路径），写发生在 UI 线程（点保存），
/// 所以 <see cref="Current"/> 返回的是**快照**而不是同一个实例 —— 调用方拿到后
/// 不会被下一次保存改掉，也不会读到写了一半的状态。
/// </summary>
public sealed class ImageSourceSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 现场手改这个文件时多写一个字段不该让程序起不来
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _path;
    private readonly ILogger<ImageSourceSettingsStore>? _logger;

    /// <summary>
    /// 读写互斥。**必须**：每帧都在检测线程读 <see cref="Current"/>，
    /// 而保存发生在 UI 线程，两者同时跑时 <c>_current</c> 是一个引用赋值 ——
    /// 引用赋值本身是原子的，加锁是为了保证"文件写完"与"内存值更新"这两步不被看见中间态。
    /// </summary>
    private readonly object _sync = new();

    /// <summary>当前生效值。只在锁内读写。</summary>
    private ImageSourceSettings _current;

    /// <summary>
    /// 构造即尝试读盘。文件不存在按"未启用"处理（这是绝大多数机器的状态，不是错误）；
    /// 文件损坏也只记日志、退回默认，**不让程序起不来** —— 一个调试辅助设置
    /// 没有资格阻止产线软件启动。
    /// </summary>
    public ImageSourceSettingsStore(string path, ILogger<ImageSourceSettingsStore>? logger = null)
    {
        _path = path;
        _logger = logger;
        _current = Load();
    }

    /// <summary>设置文件的实际路径（界面上显示给用户，方便他手工备份/拷贝）</summary>
    public string FilePath => _path;

    /// <summary>当前生效的设置（快照）</summary>
    public ImageSourceSettings Current
    {
        get
        {
            lock (_sync) return _current;
        }
    }

    /// <summary>
    /// 保存并立即生效。失败会抛 —— 对话框必须把"没存下来"告诉用户，
    /// 静默失败会让人以为改了、实际下一帧还在用旧设置。
    /// </summary>
    public void Save(ImageSourceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var copy = settings with { Folder = (settings.Folder ?? "").Trim() };

        lock (_sync)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 先写临时文件再改名：写一半断电时旧设置还在，不会留下一个坏 JSON
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(copy, JsonOptions));
            File.Move(temp, _path, overwrite: true);

            _current = copy;
        }

        _logger?.LogInformation("[图像来源] 已保存：兜底={Enabled} 目录={Folder} 递归={Recursive}",
            copy.FolderFallbackEnabled, copy.Folder, copy.Recursive);
    }

    /// <summary>
    /// 从磁盘读。**所有失败都退回 <see cref="ImageSourceSettings.Disabled"/>**，
    /// 唯一区别是留不留日志：文件不存在（首次运行）静默，读失败或 JSON 坏掉记 Warning。
    /// </summary>
    private ImageSourceSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return ImageSourceSettings.Disabled;

            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<ImageSourceSettings>(json, JsonOptions);

            return loaded is null
                ? ImageSourceSettings.Disabled
                : loaded with { Folder = (loaded.Folder ?? "").Trim() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 设置文件坏了不该让程序起不来：退回默认值并留一行日志
            _logger?.LogWarning(ex, "[图像来源] 设置文件读取失败，按未启用处理：{Path}", _path);
            return ImageSourceSettings.Disabled;
        }
    }
}
