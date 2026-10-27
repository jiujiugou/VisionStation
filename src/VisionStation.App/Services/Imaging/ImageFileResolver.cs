using System.IO;

namespace VisionStation.App.Services.Imaging;

/// <summary>
/// 把"报文说的图像位置"翻译成"硬盘上一个真的能读的文件"。
///
/// 为什么要有这一层：VM 那段路径有三种常见形态，而下游（解码/标注）只认第三种 ——
/// <list type="bullet">
/// <item>空：没配存图，报文里只有判定和数值；</item>
/// <item>目录：VM「输出图像」的"本地原图保存路径"输出给的其实是<b>文件夹</b>，
///   文件名在另一个输出项里；只订阅了前者时，报文里就只有一个目录；</item>
/// <item>完整文件：唯一能直接读的形态。</item>
/// </list>
///
/// 兜底规则不是"随便找一张能显示的图"：必须落在本帧时间附近，
/// 否则就是把上一件的图当成本件显示 —— 那比黑屏危险得多（项目里已有多处同样的取舍）。
/// </summary>
public static class ImageFileResolver
{
    /// <summary>VM「输出图像」实际会写出的扩展名</summary>
    private static readonly string[] ImageExtensions =
        [".bmp", ".jpg", ".jpeg", ".png", ".tif", ".tiff"];

    /// <summary>
    /// 兜底取到的文件，写盘时间必须落在"本帧时间 - 这个容差"以内。
    ///
    /// 取 120 秒而不是几秒，是因为要容忍：VM 与本机时钟的微小偏差、连续运行时
    /// 磁盘写入比推送慢一点、以及人工点一次执行时的操作间隔。
    /// 超过这个值就认为"目录里最新的图不是这一件的"，宁可报原因也不显示。
    /// </summary>
    public const double StaleToleranceSeconds = 120;

    /// <summary>
    /// 解析出一张可读的图像文件。
    /// </summary>
    /// <param name="reportedPath">报文里带的路径（可能是空、目录、完整文件或裸文件名）</param>
    /// <param name="frameTime">本帧报告的时间（本地时间），用于判断取到的图是不是这一件的</param>
    /// <param name="settings">界面上的图像来源设置（可能未启用）</param>
    /// <returns>成功时 Path 非空；失败时 Path 为 null 且 Reason 说明为什么、该去改哪里</returns>
    public static (string? Path, string? Reason) Resolve(
        string? reportedPath, DateTime frameTime, ImageSourceSettings settings)
    {
        var raw = (reportedPath ?? "").Trim().Trim('"');
        var folder = (settings.Folder ?? "").Trim();

        // ① 报文直接给了可读的文件 —— 这是最可信的一种，优先用
        if (raw.Length > 0 && File.Exists(raw))
            return (raw, null);

        // ② 报文给的是目录：取该目录里最新的一张
        if (raw.Length > 0 && Directory.Exists(raw))
        {
            var (file, reason) = NewestIn(raw, settings.Recursive, frameTime);
            if (file is not null) return (file, null);

            // 报文目录里没找到 → 继续试界面配置的兜底目录，两个原因都要留着
            if (settings is { FolderFallbackEnabled: true } && Directory.Exists(folder))
            {
                var (fallbackFile, fallbackReason) = NewestIn(folder, settings.Recursive, frameTime);
                return fallbackFile is not null
                    ? (fallbackFile, null)
                    : (null, $"{reason}；兜底目录：{fallbackReason}");
            }

            return (null, reason);
        }

        // ③ 报文只给了文件名（没有目录），拼到兜底目录下试试
        if (raw.Length > 0 && !Path.IsPathRooted(raw) && folder.Length > 0)
        {
            var combined = Path.Combine(folder, raw);
            if (File.Exists(combined)) return (combined, null);
        }

        // ④ 兜底目录：报文没给出任何可用位置时的最后一条路
        if (settings.FolderFallbackEnabled)
        {
            if (folder.Length == 0)
                return (null, "已启用目录兜底，但没有填目录（图像区右上角「图像来源」可以设置）");

            if (!Directory.Exists(folder))
                return (null, $"兜底目录不存在：{folder}");

            var (file, reason) = NewestIn(folder, settings.Recursive, frameTime);
            if (file is not null) return (file, null);

            return (null, $"{DescribeReported(raw)}{reason}");
        }

        // ⑤ 什么都没配
        return (null, DescribeReported(raw) + "（可在图像区右上角「图像来源」里指定一个目录）");
    }

    /// <summary>
    /// 目录里最新一张图片。给设置界面做"测试"用，也给解析用。
    /// </summary>
    /// <param name="folder">目录</param>
    /// <param name="recursive">是否含子目录</param>
    /// <param name="frameTime">本帧时间；为 null 时不做过期判断（设置界面只想看"最新的是哪张"）</param>
    public static (string? File, string? Reason) NewestIn(string folder, bool recursive, DateTime? frameTime)
    {
        FileInfo? newest = null;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                // 目录里可能混着 VM 的临时文件、系统隐藏项，读不了就跳过，不让它中断整次查找
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };

            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                if (!ImageExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)) continue;
                if (newest is null || file.LastWriteTime > newest.LastWriteTime) newest = file;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, $"读目录失败：{folder}（{ex.Message}）");
        }

        if (newest is null)
            return (null, $"目录里没有图片文件：{folder}");

        if (frameTime is { } frame)
        {
            var ageSeconds = (frame - newest.LastWriteTime).TotalSeconds;
            if (ageSeconds > StaleToleranceSeconds)
            {
                return (null,
                    $"目录里最新一张图是 {newest.LastWriteTime:HH:mm:ss} 的，比本帧旧 {ageSeconds:0} 秒"
                    + "（多半是平台没有在存图）");
            }
        }

        return (newest.FullName, null);
    }

    /// <summary>
    /// 失败文案的前半句：区分"平台压根没发路径"和"发了但用不了"。
    ///
    /// <para>
    /// 这两句话对应完全不同的现场动作：前者要去 VM 里加存图模块，
    /// 后者要去查盘符/权限/共享。合成一句"没有图像"就等于把排查方向也合掉了。
    /// </para>
    /// </summary>
    private static string DescribeReported(string raw) => raw.Length == 0
        ? "本帧结果没有带图像路径（平台侧未发 imagePath）"
        : $"平台给的图像路径不可用：{raw}";
}
