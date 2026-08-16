using System.IO;
using Microsoft.Extensions.Configuration;

namespace VisionStation.App.Services;

/// <summary>
/// 路径统一解析 —— 所有配置里的文件/目录路径都从这里解析，消除绝对路径
///
/// 规则：
///   1. 绝对路径 → 原样使用（兼容旧配置）
///   2. 相对路径 → 优先基于当前工作目录（cwd）解析：dotnet run 和发布后直接运行 exe 时，
///      cwd 就是项目/发布根目录，数据（Logs / InspectedImages / db）落在那里最直观
///   3. 读资源（模板 / 图库）在 cwd 找不到时，回退到 AppContext.BaseDirectory（bin）再找一次
///   4. 配方路径保持 BaseDirectory 优先（发布时配方可随 exe 部署），找不到再回退 cwd
///
/// 为什么不用 AppContext.BaseDirectory 作为唯一基准？
///   BaseDirectory 是 bin 目录，不适合落数据；cwd 才是"项目根 / 发布根"。
/// </summary>
public sealed class AppPaths
{
    /// <summary>File 相机读取的图像目录</summary>
    public string ImageFolder { get; }

    /// <summary>有无检测模板（PresenceTool 用）</summary>
    public string TemplatePath { get; }

    /// <summary>检测结果图片存储目录</summary>
    public string StorageFolder { get; }

    /// <summary>SQLite 数据库文件路径</summary>
    public string DbPath { get; }

    /// <summary>Serilog 日志目录</summary>
    public string LogFolder { get; }

    /// <summary>配方 JSON 文件路径</summary>
    public string RecipePath { get; }

    public AppPaths(IConfiguration config)
    {
        ImageFolder   = ResolveInput(config["Camera:ImageFolder"], "TestImages");
        TemplatePath  = ResolveInput(config["Detection:TemplatePath"], @"TestImages\hole_template.png");
        StorageFolder = ResolveOutput(config["Storage:ImageFolder"], "InspectedImages");
        DbPath        = ResolveOutput(config["Storage:DbPath"], "visionstation.db");
        LogFolder     = ResolveOutput(config["Logging:LogFolder"], "Logs");
        RecipePath    = ResolveRecipe(config["Recipe:Path"]);
    }

    /// <summary>解析只读资源（模板 / 图库）：cwd 优先，找不到回退 bin</summary>
    private static string ResolveInput(string? value, string defaultRel)
    {
        var rel = string.IsNullOrWhiteSpace(value) ? defaultRel : value;
        var cwdPath = GetFullPath(rel);
        if (!File.Exists(cwdPath) && !Directory.Exists(cwdPath))
        {
            var basePath = GetFullPath(Path.Combine(AppContext.BaseDirectory, rel));
            if (File.Exists(basePath) || Directory.Exists(basePath))
                return basePath;
        }
        return cwdPath;
    }

    /// <summary>解析输出目录 / 文件（日志 / 图片 / db）：始终基于 cwd，目录不存在时由消费方创建</summary>
    private static string ResolveOutput(string? value, string defaultRel)
    {
        var rel = string.IsNullOrWhiteSpace(value) ? defaultRel : value;
        return GetFullPath(rel);
    }

    /// <summary>解析配方路径：BaseDirectory 优先（发布时配方可随 exe 部署），再回退 cwd</summary>
    private static string ResolveRecipe(string? value)
    {
        var rel = string.IsNullOrWhiteSpace(value) ? @"Config\recipes\default.json" : value;
        var basePath = GetFullPath(Path.Combine(AppContext.BaseDirectory, rel));
        if (File.Exists(basePath)) return basePath;
        return GetFullPath(rel);
    }

    private static string GetFullPath(string rel)
        => Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, rel));
}
