using System.IO;
using VisionStation.Core.Models;
using VisionStation.Devices;

namespace VisionStation.Service;

/// <summary>
/// 启动自检
///
/// 检查项：
///   相机在线、数据库、存储路径可写、模板文件存在、配方文件合法
///
/// 被 App.xaml.cs 调用，写日志。不为零外部依赖。
/// </summary>
public class HealthChecker : IHealthChecker
{
    /// <summary>SQLite 文件头魔数（"SQLite format 3\0"，16 字节）</summary>
    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    /// <summary>ToolFactory 支持的工具类型；不在其中 = 该工具会被静默跳过</summary>
    private static readonly HashSet<string> KnownToolTypes =
        new(StringComparer.OrdinalIgnoreCase) { "Presence", "Dimension", "Defect", "Yolo" };

    private readonly DeviceManager _devices;
    private readonly string _dbPath;
    private readonly string _storagePath;
    private readonly string _templatePath;
    private readonly RecipeConfig _recipe;

    public HealthChecker(DeviceManager devices,
        string dbPath, string storagePath, string templatePath, RecipeConfig recipe)
    {
        _devices = devices;
        _dbPath = dbPath;
        _storagePath = storagePath;
        _templatePath = templatePath;
        _recipe = recipe;
    }

    /// <summary>
    /// 执行所有检查，返回结果列表
    /// 修复（R-004 / F-007）：单项检查异常被隔离为该项 FAIL，不再中断其余检查。
    /// </summary>
    public List<HealthResult> RunAll()
    {
        static HealthResult Safe(Func<HealthResult> check, string item)
        {
            try { return check(); }
            catch (Exception ex)
            {
                return new HealthResult(item, false, "检查异常",
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        return new()
        {
            Safe(CheckDevices, "设备连接"),   // 1. 相机在线
            Safe(CheckDatabase, "数据库"),    // 2. 数据库文件
            Safe(CheckStorage, "存储路径"),   // 3. 存储路径
            Safe(CheckTemplate, "模板文件"),  // 4. 模板文件
            Safe(CheckRecipe, "配方")         // 5. 配方
        };
    }

    // ════════════════════════════════════════════════
    //  检查项
    // ════════════════════════════════════════════════

    private HealthResult CheckDevices()
    {
        var (online, total) = _devices.ConnectionStats;
        // 修复（R-017 / F-038）：空设备表不得判「全部在线」——0==0 是判据失效
        bool ok = total > 0 && online == total;
        return new HealthResult("设备连接",
            ok,
            total == 0 ? "0/0 未注册任何设备" : $"{online}/{total} 在线",
            ok ? null : (total == 0 ? "未注册任何设备" : "有设备离线"));
    }

    private HealthResult CheckDatabase()
    {
        if (File.Exists(_dbPath))
        {
            // 修复（F-102）：文件存在 ≠ 可用。两件事必须实测：
            //   1) 文件头是 SQLite 魔数（挡住被覆盖成文本/半截文件）
            //   2) 能以读写方式打开（挡住被其他进程独占 —— 检测记录写不进去）
            try
            {
                var head = new byte[16];
                int n;
                using (var fs = new FileStream(_dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    n = fs.Read(head, 0, head.Length);

                if (n < 16 || !head.AsSpan().SequenceEqual(SqliteMagic))
                    return new HealthResult("数据库", false, _dbPath, "文件存在但不是有效的 SQLite 库");
            }
            catch (Exception ex)
            {
                return new HealthResult("数据库", false, _dbPath, $"数据库文件无法读取（{ex.GetType().Name}）");
            }

            try
            {
                using var _ = new FileStream(_dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch (Exception ex)
            {
                return new HealthResult("数据库", false, _dbPath, $"数据库文件被占用（{ex.GetType().Name}）");
            }

            return new HealthResult("数据库", true, _dbPath);
        }

        // 修复（R-001 / F-001）：库文件不存在 → 探测父目录可写
        // （SQLite 首次运行自动建库，所以「缺文件」本身不是故障，「目录不可写」才是）。
        var dir = Path.GetDirectoryName(_dbPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return new HealthResult("数据库", false, _dbPath, "数据库路径不可达");

        try
        {
            var probe = Path.Combine(dir, ".db_write_test");
            File.WriteAllText(probe, "t");
            File.Delete(probe);
            return new HealthResult("数据库", true, _dbPath);
        }
        catch
        {
            return new HealthResult("数据库", false, _dbPath, "数据库目录不可写");
        }
    }

    private HealthResult CheckStorage()
    {
        if (!Directory.Exists(_storagePath))
        {
            try { Directory.CreateDirectory(_storagePath); }
            catch { return new HealthResult("存储路径", false, _storagePath, "无法创建目录"); }
        }

        try
        {
            var testFile = Path.Combine(_storagePath, ".write_test");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return new HealthResult("存储路径", true, _storagePath);
        }
        catch
        {
            return new HealthResult("存储路径", false, _storagePath, "目录不可写");
        }
    }

    private HealthResult CheckTemplate()
    {
        if (string.IsNullOrEmpty(_templatePath))
            return new HealthResult("模板文件", false, "未配置", "TemplatePath 为空");

        if (!File.Exists(_templatePath))
            return new HealthResult("模板文件", false, _templatePath, "文件不存在");

        // 修复（F-103）：文件存在 ≠ 可解码。只读文件头魔数，不解码、不引第三方库。
        // 挡住「改了扩展名的文本 / 半截下载文件」——这类文件在运行期才会炸，停机点太晚。
        if (!LooksLikeImage(_templatePath))
            return new HealthResult("模板文件", false, _templatePath, "文件存在但不是可识别的图像");

        return new HealthResult("模板文件", true, _templatePath);
    }

    /// <summary>按文件头判断是否为常见图像（BMP / PNG / JPEG / RIFF-WEBP）</summary>
    private static bool LooksLikeImage(string path)
    {
        Span<byte> head = stackalloc byte[12];
        int n;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            n = fs.Read(head);
        }
        catch { return false; }

        if (n >= 2 && head[0] == (byte)'B' && head[1] == (byte)'M') return true;                     // BMP
        if (n >= 4 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return true; // PNG
        if (n >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return true;            // JPEG
        if (n >= 4 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F') return true; // WEBP
        return false;
    }

    private HealthResult CheckRecipe()
    {
        if (_recipe == null)
            return new HealthResult("配方文件", false, "空", "配方为空");

        if (_recipe.Tools.Count == 0)
            return new HealthResult("配方文件", false, "空", "配方无效或为空");

        // 修复（F-101）：原来只查 Tools.Count > 0，于是「类型为空 / 引用了不存在的 ROI /
        // 标定值为 0」全部放行，缺陷到运行期才炸（且不报错，只是判定结果悄悄变错）。
        var problems = new List<string>();

        double ppm = _recipe.Calibration?.PixelPerMillimeter ?? 0;
        if (ppm <= 0)
            problems.Add($"标定值非法 PixelPerMillimeter={ppm}");

        var roiNames = new HashSet<string>(
            _recipe.Rois.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var t in _recipe.Tools.Where(t => t.Enabled))
        {
            if (string.IsNullOrWhiteSpace(t.Type))
            {
                problems.Add("存在工具未指定类型");
                continue;
            }
            if (!KnownToolTypes.Contains(t.Type))
                problems.Add($"未知工具类型 {t.Type}");

            // Yolo 允许不绑 ROI（它自己出框），其余工具必须绑到存在的 ROI
            if (!string.IsNullOrWhiteSpace(t.Roi) && !roiNames.Contains(t.Roi))
                problems.Add($"{t.Type} 引用了不存在的 ROI: {t.Roi}");
        }

        bool ok = problems.Count == 0;
        return new HealthResult("配方文件",
            ok,
            ok ? $"{_recipe.Name} · {_recipe.Tools.Count}个工具" : string.Join("；", problems.Take(3)),
            ok ? null : "配方不合法");
    }
}

/// <summary>
/// 单条自检结果
/// </summary>
public class HealthResult
{
    public string Item { get; }        // "相机"
    public bool OK { get; }            // ✅/❌
    public string Detail { get; }      // "1/2 在线"
    public string? Error { get; }      // null = 没问题

    public HealthResult(string item, bool ok, string detail, string? error = null)
    {
        Item = item; OK = ok; Detail = detail; Error = error;
    }

    public override string ToString()
        => $"[{(OK ? "✅" : "❌")}] {Item}: {Detail}{(Error != null ? $" — {Error}" : "")}";
}
