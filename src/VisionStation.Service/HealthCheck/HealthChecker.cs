using System.IO;
using VisionStation.Core.Models;
using VisionStation.Devices;

namespace VisionStation.Service;

/// <summary>
/// 启动自检
///
/// 检查项：相机在线、数据库、存储路径、检测引擎资源、配方合法。
///
/// 与改造前的区别：原来的"模板文件"检查变成了"检测引擎"检查 ——
/// 模板是自研 PresenceTool 的输入，现在引擎的输入是 .sol 流程文件或 ONNX 模型。
/// 检查的对象变了，但目的没变：**在启动时就把"跑不起来的配置"指出来**，
/// 而不是等第一帧检测时才炸，还炸得不知所云。
/// </summary>
public class HealthChecker : IHealthChecker
{
    /// <summary>SQLite 文件头魔数（"SQLite format 3\0"，16 字节）</summary>
    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    private readonly DeviceManager _devices;
    private readonly string _dbPath;
    private readonly string _storagePath;
    private readonly RecipeConfig? _recipe;
    private readonly string _assetRoot;

    /// <summary>引擎运行时本身不可用时的原因（如本机没装 VM SDK），null = 正常</summary>
    private readonly string? _engineRuntimeProblem;

    /// <param name="devices">设备管理器</param>
    /// <param name="dbPath">数据库文件</param>
    /// <param name="storagePath">图片存储目录</param>
    /// <param name="recipe">已加载的配方（可空：部分探针测试只验单项检查）</param>
    /// <param name="assetRoot">引擎资源的解析基准目录（exe 目录）</param>
    /// <param name="engineRuntimeProblem">引擎运行时缺失的原因（可选）</param>
    public HealthChecker(DeviceManager devices,
        string dbPath, string storagePath, RecipeConfig? recipe, string assetRoot,
        string? engineRuntimeProblem = null)
    {
        _devices = devices;
        _dbPath = dbPath;
        _storagePath = storagePath;
        _recipe = recipe;
        _assetRoot = assetRoot;
        _engineRuntimeProblem = engineRuntimeProblem;
    }

    /// <summary>
    /// 执行所有检查，返回结果列表
    /// 单项检查异常被隔离为该项 FAIL —— 一个设备坏了不该让整份自检结果拿不到。
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
            Safe(CheckEngine, "检测引擎"),    // 4. 引擎资源（.sol / 模型）
            Safe(CheckRecipe, "配方")         // 5. 配方
        };
    }

    // ════════════════════════════════════════════════
    //  检查项
    // ════════════════════════════════════════════════

    private HealthResult CheckDevices()
    {
        var (online, total) = _devices.ConnectionStats;
        // 空设备表不能判「全部在线」：0==0 会让判据失效，必须要求 total > 0。
        bool ok = total > 0 && online == total;
        if (ok)
            return new HealthResult("设备连接", true, $"{online}/{total} 在线");

        if (total == 0)
            return new HealthResult("设备连接", false, "0/0 未注册任何设备", "未注册任何设备");

        // 离线时把每台的原因带出来，而不是一句"有设备离线"——
        // "目录不存在"和"设备被独占"的排查方向完全不同，笼统的离线会把人引向查线。
        var reasons = _devices.Devices
            .Where(d => !d.IsConnected)
            .Select(d => $"{d.Name}: {d.LastError ?? "离线（未记录原因）"}");
        return new HealthResult("设备连接",
            false,
            $"{online}/{total} 在线",
            string.Join("；", reasons));
    }

    private HealthResult CheckDatabase()
    {
        if (File.Exists(_dbPath))
        {
            // 文件存在 ≠ 可用。两件事必须实测：
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

        // 库文件不存在时探测父目录可写
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

    /// <summary>
    /// 检测引擎检查：引擎资源在不在、运行时有没有缺。
    ///
    /// 这三件事分别对应三种完全不同的故障：
    ///   1. 资源缺失 —— 工程师没把 .sol / 模型拷过来（部署问题）
    ///   2. 运行时缺失 —— 本机没装 VM 或没部署其运行时（环境问题）
    ///   3. 配方非法 —— 引擎名/参数写错（配置问题）
    /// 混成一句"检测失败"的话，现场要在三种方向之间乱试。
    /// </summary>
    private HealthResult CheckEngine()
    {
        if (_recipe is null)
            return new HealthResult("检测引擎", false, "未加载配方", "配方为空，无法确定用哪个引擎");

        var isVm = string.Equals(_recipe.Engine, EngineNames.Vm, StringComparison.OrdinalIgnoreCase);
        var label = isVm ? "VM 流程文件" : "ONNX 模型";

        var configured = isVm ? _recipe.Vm.SolutionPath : _recipe.Onnx.ModelPath;
        if (string.IsNullOrWhiteSpace(configured))
            return new HealthResult("检测引擎", false, _recipe.Engine, $"{label}路径未配置");

        var path = RecipeConfig.ResolveAsset(configured, _assetRoot);

        if (!File.Exists(path))
            return new HealthResult("检测引擎", false, $"{_recipe.Engine} · {path}",
                $"{label}不存在");

        try
        {
            if (new FileInfo(path).Length == 0)
                return new HealthResult("检测引擎", false, $"{_recipe.Engine} · {path}", $"{label}是空文件");
        }
        catch (IOException ex)
        {
            return new HealthResult("检测引擎", false, $"{_recipe.Engine} · {path}", $"文件无法读取（{ex.GetType().Name}）");
        }

        // 运行时缺失优先报：此时即使文件齐了也跑不起来，先说最致命的那条
        if (_engineRuntimeProblem is { } problem)
            return new HealthResult("检测引擎", false, $"{_recipe.Engine} · {Path.GetFileName(path)}", problem);

        return new HealthResult("检测引擎", true, $"{_recipe.Engine} · {Path.GetFileName(path)}");
    }

    private HealthResult CheckRecipe()
    {
        if (_recipe == null)
            return new HealthResult("配方文件", false, "空", "配方为空");

        // 校验规则本身在 Core（RecipeConfig.Validate），这里只负责把它说给操作员听。
        // 规则写在两处迟早会对不上 —— 这是"单一真相源"最容易被破坏的地方之一。
        var problems = _recipe.Validate();
        bool ok = problems.Count == 0;

        return new HealthResult("配方文件",
            ok,
            ok ? $"{_recipe.Name} · {_recipe.Engine}" : string.Join("；", problems.Take(3)),
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
