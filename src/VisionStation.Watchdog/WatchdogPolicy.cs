using System.Text.Json;

namespace VisionStation.Watchdog;

// 看门狗的三块纯逻辑，从 Program.cs 的顶级语句里提出来。
// 为什么值得单独成文件：它们是 F-50 四个能力（重启判定 / 崩溃循环保护 / 指数退避 / 参数重放）
// 里**能脱离进程与内核对象单独验证**的部分 —— 守护循环本身要真进程才能测，
// 而这三块决定了"该不该重启、多久重启、按什么配置重启"，错了的后果都是现场事故。

/// <summary>
/// 参数重放：从看门狗自己的 argv 里切出"属于主程序、崩溃重启时要原样重放"的那一段。
///
/// <para>
/// 归属判断依据（只依据本文件与 App.StartWatchdog 的实现，不靠名字猜）：
/// argv[0] = 被守护进程的 PID（看门狗自己的，绝不转交——主程序拿到裸数字没有意义，
/// 还会顶掉真正的第一个参数）；argv[1] = 主程序 exe 全路径（重启时由
/// ProcessStartInfo.FileName 单独消费，再传一遍就成了"主程序收到自己的路径"）；
/// argv[2..] = 主程序原始命令行，原样转交。
/// </para>
///
/// <para>
/// 看门狗自身没有任何具名开关（没有 --watch / --verbose 之类），所以不存在
/// "看着像主程序参数、其实是看门狗的"这种歧义 —— 这是能按位置简单切分的前提。
/// 主程序侧用 "--" 显式分隔（App.StartWatchdog 固定追加），本方法优先认它；
/// 没有 "--" 时全部照转，兼容直接追加参数的旧写法。
/// </para>
///
/// <para>
/// 为什么要重放：--data-root 决定数据根（db / 日志都落在它下面）。丢了它，被拉起的新进程
/// 会回落成"数据根 = exe 所在目录"，现场表现为"崩溃重启之后数据丢了"—— 数据其实还在
/// 原来那块盘上，只是新进程在看另一个空目录。不报错、无异常，是最难排查的一类问题。
/// </para>
/// </summary>
public static class WatchdogArgs
{
    /// <summary>看门狗自消费的位置参数个数：argv[0] = PID、argv[1] = exe 路径</summary>
    public const int OwnArgCount = 2;

    /// <summary>切出主程序参数。没有主程序参数时返回空数组（不是 null）</summary>
    public static string[] TakeMainArgs(IReadOnlyList<string> args)
    {
        if (args.Count <= OwnArgCount) return Array.Empty<string>();

        var rest = new string[args.Count - OwnArgCount];
        for (var i = 0; i < rest.Length; i++) rest[i] = args[OwnArgCount + i];

        // "--" 之后的才是主程序参数，分隔符本身不转交（与常规 CLI 约定一致）。
        return rest[0] == "--" ? rest[1..] : rest;
    }
}

/// <summary>
/// 崩溃循环保护 + 指数退避：滑动窗口内记账，窗口内重启次数超限就踩刹车。
///
/// <para>
/// <b>为什么要有窗口</b>：这是"确定性崩溃"（配方损坏、端口被占、依赖缺失）的唯一刹车 ——
/// 没有它，一台无人值守的机器会整夜"拉起 → 崩溃 → 拉起"，日志刷满、磁盘写满，
/// 第二天来看不出最初那条错误。
/// </para>
///
/// <para>
/// <b>计数口径</b>：只记窗口内的崩溃次数，窗口外的旧账在每次记账时顺手清掉 ——
/// 所以"跑了一天崩一次"永远不会触发保护，只有密集崩溃才会。
/// </para>
/// </summary>
public sealed class RestartPolicy
{
    private readonly WatchdogConfig _cfg;
    private readonly Queue<DateTime> _history = new();

    public RestartPolicy(WatchdogConfig cfg) => _cfg = cfg;

    /// <summary>当前窗口内的崩溃次数（Register 之后才有意义）</summary>
    public int WindowCount => _history.Count;

    /// <summary>窗口内次数达到上限 —— 停止自动拉起，留 CRITICAL 等人工介入</summary>
    public bool ShouldStop => _history.Count >= _cfg.MaxRestarts;

    /// <summary>
    /// 记一次崩溃，并清掉窗口外的旧账。
    /// 返回清理后窗口内的次数（= <see cref="WindowCount"/>，方便日志一次取用）。
    /// </summary>
    public int Register(DateTime now)
    {
        _history.Enqueue(now);
        while (_history.Count > 0 && (now - _history.Peek()).TotalMinutes > _cfg.WindowMinutes)
            _history.Dequeue();
        return _history.Count;
    }

    /// <summary>
    /// 下一次重启的退避秒数：基准 × 2^(窗口内第几次-1)，封顶 BackoffMaxSeconds。
    /// 指数给瞬时故障自愈的机会，封顶防止第 10 次退避拖到 17 分钟。
    /// </summary>
    public int NextDelaySeconds
        => (int)Math.Min(_cfg.RestartDelaySeconds * Math.Pow(2, _history.Count - 1), _cfg.BackoffMaxSeconds);
}

/// <summary>看门狗策略：从 exe 同目录的 appsettings.json 的 Watchdog 节读取，缺失用默认值</summary>
/// <param name="MaxRestarts">窗口期内最多拉起几次；达到即停止并留 CRITICAL（默认 3）</param>
/// <param name="WindowMinutes">滑动窗口长度，单位分钟（默认 10）。窗口外的重启记录会被丢掉</param>
/// <param name="RestartDelaySeconds">第一次退避的基准秒数（默认 2），之后按 2 的幂增长</param>
/// <param name="BackoffMaxSeconds">单次退避上限（默认 60）。防止指数增长把恢复时间拖到不可接受</param>
public sealed record WatchdogConfig(int MaxRestarts, double WindowMinutes, int RestartDelaySeconds, int BackoffMaxSeconds)
{
    /// <summary>
    /// 读配置。<b>任何异常都回落到默认值</b>，这与主程序的策略相反
    /// （<c>AppOptionsBinder</c> 是"配置非法就启动失败"）—— 因为这里的角色不同：
    /// 看门狗是最后一道保险，配置写坏了它也必须照样守护，不能跟着主程序一起不启动。
    /// </summary>
    /// <param name="path">
    /// 配置文件路径；null = 用 exe 同目录的 appsettings.json。
    /// 参数只给测试用：测试没有 exe 目录的控制权，得指一个临时文件进来。
    /// </param>
    public static WatchdogConfig Load(string? path = null)
    {
        try
        {
            path ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("Watchdog", out var w))
                {
                    return new WatchdogConfig(
                        GetInt(w, "MaxRestarts", 3),
                        GetInt(w, "WindowMinutes", 10),
                        GetInt(w, "RestartDelaySeconds", 2),
                        GetInt(w, "BackoffMaxSeconds", 60));
                }
            }
        }
        catch { /* 配置坏了走默认策略 */ }

        // 兜底值与 appsettings.json 里的 Watchdog 段保持一致：
        // 现场看到"行为和配置对不上"时，先确认这两个地方有没有被改歪。
        return new WatchdogConfig(3, 10, 2, 60);
    }

    /// <summary>
    /// 读一个整数键，缺键/类型不对/解析失败都回落到 <paramref name="def"/>，且**只影响这一个键**
    /// （非数值的 ValueKind 必须先挡掉：对字符串调 TryGetInt32 会抛 InvalidOperationException，
    /// 异常一旦冒出去，整个配置会全部回落默认，把没写错的键也牵连了）。
    /// 与主程序"数值解析失败要带着键名报错"不同：看门狗宁可带着默认值工作，
    /// 也不要因为一个错字而不守护（它存在的意义就是"别的东西出错时它还在"）。
    /// </summary>
    private static int GetInt(JsonElement e, string name, int def)
        => e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out var i)
            ? i : def;
}
