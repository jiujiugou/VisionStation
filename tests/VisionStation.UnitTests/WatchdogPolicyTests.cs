using VisionStation.Watchdog;

namespace VisionStation.UnitTests;

/// <summary>
/// F-50 看门狗四能力的纯逻辑验证（守护循环本身要真进程，见 Watchdog/Program.cs 的注释）。
///
/// <para>
/// 覆盖映射：
/// ① 参数重放（<see cref="WatchdogArgs.TakeMainArgs"/>）—— 崩溃重启时 <c>--data-root</c> 不丢，
///    丢了的表现是"重启之后数据没了"且无任何报错；
/// ② 崩溃循环保护（<see cref="RestartPolicy.Register"/> + <see cref="RestartPolicy.ShouldStop"/>）——
///    窗口内超限踩刹车，防"确定性崩溃 → 拉起 → 再崩"整夜抖动；
/// ③ 指数退避（<see cref="RestartPolicy.NextDelaySeconds"/>）—— 2s→4s→8s…，封顶；
/// ④ 配置兜底（<see cref="WatchdogConfig.Load"/>）—— 配置坏了也必须照样守护，回落默认值。
/// </para>
///
/// <para>
/// 这些逻辑原来散在顶级语句里没有类名，测试工程够不着 —— 2026-10-28 提取成
/// <c>WatchdogPolicy.cs</c> 后才可测。行为与提取前逐行一致。
/// </para>
/// </summary>
public class WatchdogPolicyTests
{
    // ───────────────────────── ① 参数重放 ─────────────────────────

    [Fact]
    public void 只有PID和exe路径_主程序参数为空()
    {
        // 主程序启动时没带任何参数的形态
        var result = WatchdogArgs.TakeMainArgs(new[] { "1234", @"C:\bin\VisionStation.App.exe" });

        Assert.Empty(result);
    }

    [Fact]
    public void 带分隔符_之后全部转交且分隔符不转交()
    {
        // App.StartWatchdog 的固定形态：PID、exe、"@"分隔、主程序原始参数
        var result = WatchdogArgs.TakeMainArgs(new[]
        {
            "1234", @"C:\bin\VisionStation.App.exe", "--", "--data-root", @"D:\产线数据",
        });

        Assert.Equal(new[] { "--data-root", @"D:\产线数据" }, result);
    }

    [Fact]
    public void 不带分隔符_全部照转_兼容旧写法()
    {
        var result = WatchdogArgs.TakeMainArgs(new[]
        {
            "1234", @"C:\bin\VisionStation.App.exe", "--data-root", @"D:\产线数据",
        });

        Assert.Equal(new[] { "--data-root", @"D:\产线数据" }, result);
    }

    [Fact]
    public void 含空格的路径_保持单条参数不拆散()
    {
        // 钉住"不做二次拆分"：重放按参数数组逐条转交，拆散一条 = 两个错参数
        var result = WatchdogArgs.TakeMainArgs(new[]
        {
            "1234", @"C:\bin\VisionStation.App.exe", "--data-root", @"D:\My Data\VS 产线\",
        });

        Assert.Equal(new[] { "--data-root", @"D:\My Data\VS 产线\" }, result);
    }

    // ───────────────────── ② 崩溃循环保护 ─────────────────────

    private static readonly WatchdogConfig 默认配置 = new(MaxRestarts: 3, WindowMinutes: 10, RestartDelaySeconds: 2, BackoffMaxSeconds: 60);

    [Fact]
    public void 窗口内达到上限_停止拉起()
    {
        var policy = new RestartPolicy(默认配置);
        var t = DateTime.Now;

        policy.Register(t);
        policy.Register(t.AddSeconds(30));
        Assert.False(policy.ShouldStop);    // 第 2 次：还在容忍范围内

        policy.Register(t.AddSeconds(60));  // 第 3 次 = MaxRestarts
        Assert.True(policy.ShouldStop);
    }

    [Fact]
    public void 窗口外的旧账被清理_密集崩溃才算循环()
    {
        // 跑一天崩一次，永远不该触发保护 —— 只有 10 分钟内的密集崩溃才是"循环"
        var policy = new RestartPolicy(默认配置);
        var t = DateTime.Now;

        policy.Register(t);
        policy.Register(t.AddMinutes(11));
        policy.Register(t.AddMinutes(22));

        Assert.Equal(1, policy.WindowCount);   // 前两次都掉出 10 分钟窗口
        Assert.False(policy.ShouldStop);
    }

    [Fact]
    public void 窗口边界上的记录_恰好等于窗口长度时被清出()
    {
        // 口径钉死：(now - peek) > WindowMinutes 才清 —— 恰好 10 分钟的记录还在窗口内
        var policy = new RestartPolicy(默认配置);
        var t = DateTime.Now;

        policy.Register(t);
        policy.Register(t.AddMinutes(10).AddSeconds(-1));

        Assert.Equal(2, policy.WindowCount);
    }

    // ───────────────────── ③ 指数退避 ─────────────────────

    [Fact]
    public void 退避按2的幂增长()
    {
        var policy = new RestartPolicy(默认配置);
        var t = DateTime.Now;

        policy.Register(t);
        Assert.Equal(2, policy.NextDelaySeconds);          // 2^0 × 2

        policy.Register(t.AddSeconds(5));
        Assert.Equal(4, policy.NextDelaySeconds);          // 2^1 × 2

        policy.Register(t.AddSeconds(10));
        Assert.Equal(8, policy.NextDelaySeconds);          // 2^2 × 2
    }

    [Fact]
    public void 退避封顶不超BackoffMax()
    {
        var policy = new RestartPolicy(new WatchdogConfig(MaxRestarts: 10, WindowMinutes: 10, RestartDelaySeconds: 2, BackoffMaxSeconds: 60));
        var t = DateTime.Now;

        for (var i = 0; i < 8; i++) policy.Register(t.AddSeconds(i));

        // 不封顶的话第 7 次是 2^6 × 2 = 128 秒
        Assert.Equal(60, policy.NextDelaySeconds);
    }

    [Fact]
    public void 旧账掉出窗口后退避回落到基准()
    {
        // 窗口清空后 = "窗口内第 1 次崩溃"，退避回到基准，不带着历史包袱
        var policy = new RestartPolicy(默认配置);
        var t = DateTime.Now;

        policy.Register(t);
        policy.Register(t.AddSeconds(5));
        policy.Register(t.AddMinutes(20));  // 前两次掉出窗口

        Assert.Equal(1, policy.WindowCount);
        Assert.Equal(2, policy.NextDelaySeconds);
    }

    // ───────────────────── ④ 配置兜底 ─────────────────────

    [Fact]
    public void 配置正常时_逐键解析()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wd-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path,
                """{ "Watchdog": { "MaxRestarts": 5, "WindowMinutes": 20, "RestartDelaySeconds": 3, "BackoffMaxSeconds": 90 } }""");

            var cfg = WatchdogConfig.Load(path);

            Assert.Equal(new WatchdogConfig(5, 20, 3, 90), cfg);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 文件不存在_回落默认()
    {
        var cfg = WatchdogConfig.Load(Path.Combine(Path.GetTempPath(), $"wd-不存在-{Guid.NewGuid():N}.json"));

        Assert.Equal(new WatchdogConfig(3, 10, 2, 60), cfg);
    }

    [Fact]
    public void JSON损坏_回落默认_不抛异常()
    {
        // 看门狗是最后一道保险：配置写坏了它也必须照样守护，不能跟着配置一起死
        var path = Path.Combine(Path.GetTempPath(), $"wd-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ Watchdog: !!! 不是 JSON !!!");

            var cfg = WatchdogConfig.Load(path);

            Assert.Equal(new WatchdogConfig(3, 10, 2, 60), cfg);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 单键类型错误_该键回落默认_其他键正常()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wd-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path,
                """{ "Watchdog": { "MaxRestarts": "三个", "WindowMinutes": 20 } }""");

            var cfg = WatchdogConfig.Load(path);

            Assert.Equal(3, cfg.MaxRestarts);    // 错字 → 默认
            Assert.Equal(20, cfg.WindowMinutes); // 正常键不受牵连
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 缺Watchdog节_整体回落默认()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wd-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "Logging": { "LogLevel": "Information" } }""");

            var cfg = WatchdogConfig.Load(path);

            Assert.Equal(new WatchdogConfig(3, 10, 2, 60), cfg);
        }
        finally { File.Delete(path); }
    }
}
