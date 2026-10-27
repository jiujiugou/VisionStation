using System.IO;
using VisionStation.App.Composition;

namespace VisionStation.App.Services;

/// <summary>
/// 路径统一解析（W-15 拍板后的便携模式）。所有配置里的文件/目录路径都从这里解析：
///
/// 规则：
///   1. 绝对路径 → 原样使用（走查脚本、现场自定义都在用）
///   2. 相对路径 → 读资源基于 exe 目录（<see cref="AppContext.BaseDirectory"/>，资源随 exe 部署），
///      找不到时回退 cwd 一次 —— 只为开发期 <c>dotnet run</c> 便利，资源只读、不落任何数据
///   3. 可写数据（db / 日志 / 检测图片）→ 一律基于 <see cref="AppOptions.DataRoot"/>，
///      默认就是 exe 目录。**绝不使用 Environment.CurrentDirectory**：
///      快捷方式"起始位置"、计划任务、服务启动都会把 cwd 指到任意地方
///
/// 便携的含义：整个发布目录拷到别的盘/机器，数据跟着目录走；
/// 想把数据挪走用启动参数 <c>--data-root=路径</c> 显式声明，不靠猜。
/// </summary>
public sealed class AppPaths
{
    /// <summary>可写数据根（由 AppOptions 解析好的绝对路径）</summary>
    public string DataRoot { get; }

    /// <summary>
    /// 检测引擎资源（.sol 流程文件、ONNX 模型）的解析基准目录。
    ///
    /// 用 exe 目录而不是 cwd：流程文件与模型随程序部署，和 exe 同生共死。
    /// 配方里写相对路径时都相对它解析（RecipeConfig.ResolveAsset）。
    /// </summary>
    public string AssetRoot { get; }

    /// <summary>检测结果图片存储目录</summary>
    public string StorageFolder { get; }

    /// <summary>SQLite 数据库文件路径</summary>
    public string DbPath { get; }

    /// <summary>Serilog 日志目录</summary>
    public string LogFolder { get; }

    /// <summary>配方 JSON 文件路径</summary>
    public string RecipePath { get; }

    /// <summary>
    /// 图像来源设置（界面上「图像来源」窗口写的那份）。
    ///
    /// 放在可写数据根下、不进资源目录：它是**这台机器的现场设置**，
    /// 不是随程序发布的只读资源 —— 换一台工控机就该是另一份。
    /// </summary>
    public string ImageSourceSettingsPath { get; }

    /// <summary>
    /// 配方目录（S7：同一目录下的多个 json 就是多份配方，靠 .active 指针切换）。
    /// 由 RecipePath 推导，配置里仍然只写一个文件路径 —— 不新增配置键。
    /// </summary>
    public string RecipeFolder => Path.GetDirectoryName(RecipePath) ?? DataRoot;

    public AppPaths(AppOptions options)
    {
        DataRoot      = options.DataRoot;
        AssetRoot     = AppContext.BaseDirectory;
        StorageFolder = ResolveOutput(options.Storage.ImageFolder, "InspectedImages");
        DbPath        = ResolveOutput(options.Storage.DbPath, "visionstation.db");
        LogFolder     = ResolveOutput(options.Logging.LogFolder, "Logs");
        RecipePath    = ResolveRecipe(options.Recipe.Path);

        ImageSourceSettingsPath = ResolveOutput(string.Empty, @"Config\image-source.json");
    }

    /// <summary>解析可写数据：只认 DataRoot，目录不存在时由消费方创建</summary>
    private string ResolveOutput(string value, string defaultRel)
    {
        var rel = string.IsNullOrWhiteSpace(value) ? defaultRel : value;
        return Path.IsPathRooted(rel) ? rel : GetFullPath(DataRoot, rel);
    }

    /// <summary>解析配方路径：exe 目录优先（发布时配方可随 exe 部署），找不到再回退 cwd</summary>
    private string ResolveRecipe(string value)
    {
        var rel = string.IsNullOrWhiteSpace(value) ? @"Config\recipes\default.json" : value;
        if (Path.IsPathRooted(rel)) return rel;

        var basePath = GetFullPath(AppContext.BaseDirectory, rel);
        if (File.Exists(basePath)) return basePath;

        return GetFullPath(Environment.CurrentDirectory, rel);
    }

    private static string GetFullPath(string baseDir, string rel)
        => Path.GetFullPath(Path.Combine(baseDir, rel));
}
