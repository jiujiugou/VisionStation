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
    /// </summary>
    public List<HealthResult> RunAll()
    {
        return new()
        {
            CheckDevices(),     // 1. 相机在线
            CheckDatabase(),    // 2. 数据库文件
            CheckStorage(),     // 3. 存储路径
            CheckTemplate(),    // 4. 模板文件
            CheckRecipe()       // 5. 配方
        };
    }

    // ════════════════════════════════════════════════
    //  检查项
    // ════════════════════════════════════════════════

    private HealthResult CheckDevices()
    {
        var (online, total) = _devices.ConnectionStats;
        bool ok = online == total;
        return new HealthResult("设备连接",
            ok, $"{online}/{total} 在线", ok ? null : "有设备离线");
    }

    private HealthResult CheckDatabase()
    {
        bool ok = File.Exists(_dbPath) || Directory.Exists(Path.GetDirectoryName(_dbPath));
        // SQLite 会自动创建，这里只检查目录可达
        return new HealthResult("数据库",
            ok, _dbPath, ok ? null : "数据库路径不可达");
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

        bool ok = File.Exists(_templatePath);
        return new HealthResult("模板文件",
            ok, _templatePath, ok ? null : "文件不存在");
    }

    private HealthResult CheckRecipe()
    {
        bool ok = _recipe != null && _recipe.Tools.Count > 0;
        return new HealthResult("配方文件",
            ok,
            ok ? $"{_recipe!.Name} · {_recipe.Tools.Count}个工具" : "空",
            ok ? null : "配方无效或为空");
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
