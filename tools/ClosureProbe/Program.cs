using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using VisionStation.App.Composition;
using VisionStation.Devices;
using VisionStation.Storage;

namespace ClosureProbe;

/// <summary>
/// 闭环工装：不用界面，跑真实组合根 + 真实检测链路，然后往里面注入"产线上会发生的意外"。
///
/// 为什么不用点界面：本机 Add-Type / UIAutomation 被安全策略拦截，无法点按钮；
/// 而这一轮要验的不是按钮响应，是**链路在异常下的表现**。
/// 所以这里走 <see cref="AppHost.InitializeAsync"/> —— 与真启动完全同一条路径
/// （配置绑定、设备连接、自检、VM 订阅、建库），只是不 Show 窗口。
///
/// 一次进程只跑一个场景：场景之间不共享静态日志、崩溃也不会污染别的结论。
/// 用法：ClosureProbe.exe base | no-image-dir | corrupt-image | sensor-mismatch |
///                             image-dir-is-file | recipe-corrupt | fast-tick | db-locked
/// </summary>
internal static class Program
{
    private const string RepoRoot = @"D:\Code\VisionStation";
    private static readonly string SourceImages =
        Path.Combine(RepoRoot, "TestImages", "VirtualCamera", "2448x2048");
    private static readonly string SourceRecipes =
        Path.Combine(RepoRoot, "src", "VisionStation.App", "Config", "recipes");

    private static long _fired;
    private static long _peakBytes;

    /// <summary>
    /// 必须在真 UI 线程上跑：组合根会在 AddViews 里检查 SynchronizationContext，
    /// 拿不到就直接拒绝启动（这是刻意的护栏 —— 没有上下文，检测结果会回到线程池去更新界面）。
    /// 所以这里起一个 Dispatcher 并让它泵消息，而不是绕开那条检查。
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var code = 0;
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));

        dispatcher.InvokeAsync(async () =>
        {
            try { code = await RunAsync(args); }
            catch (Exception ex) { Console.WriteLine($"{{\"fatal\":\"{ex.GetType().Name}: {ex.Message.Replace("\"", "'")}\"}}"); code = 3; }
            finally { dispatcher.InvokeShutdown(); }
        });

        System.Windows.Threading.Dispatcher.Run();
        return code;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var name = args.Length > 0 ? args[0] : "base";
        var runRoot = Path.Combine(RepoRoot, "artifacts", "runs", name);
        ResetDirectory(runRoot);

        var images = Path.Combine(runRoot, "images");
        Directory.CreateDirectory(images);
        foreach (var f in Directory.GetFiles(SourceImages, "*.bmp"))
            File.Copy(f, Path.Combine(images, Path.GetFileName(f)));

        // 配方复制一份到运行目录：场景要破坏它，不能动仓库里的原件。
        var recipes = Path.Combine(runRoot, "recipes");
        CopyDirectory(SourceRecipes, recipes);
        var recipePath = Path.Combine(recipes, "default-2448.json");

        var settings = new Dictionary<string, object?>
        {
            ["Camera"] = new Dictionary<string, object?>
            {
                ["Mode"] = "File",
                ["ImageFolder"] = images,
                ["FileFilter"] = "*.jpg;*.png;*.bmp",
                ["Loop"] = true
            },
            ["Trigger"] = new Dictionary<string, object?> { ["TimerIntervalMs"] = 300 },
            ["Recipe"] = new Dictionary<string, object?> { ["Path"] = recipePath },
            ["Detection"] = new Dictionary<string, object?>
            {
                ["TemplatePath"] = Path.Combine(RepoRoot, "TestImages", "hole_template_2448.png")
            },
            ["Storage"] = new Dictionary<string, object?>
            {
                ["Mode"] = "All",
                ["ImageFolder"] = "InspectedImages",
                ["DbPath"] = "visionstation.db"
            },
            ["Housekeeping"] = new Dictionary<string, object?>
            {
                ["MemoryCheckIntervalMs"] = 60000,
                ["MaxMemoryMB"] = 500,
                ["DatabaseCleanIntervalMs"] = 3600000,
                ["KeepRecordsDays"] = 30,
                ["ImageRetentionDays"] = 30,
                ["MaxImageFolderMB"] = 1000
            },
            ["Logging"] = new Dictionary<string, object?>
            {
                ["LogFolder"] = Path.Combine(runRoot, "logs")
            }
        };

        // 场景改动（启动前）
        var runSeconds = 6.0;
        switch (name)
        {
            case "no-image-dir":
                Set(settings, "Camera", "ImageFolder", Path.Combine(runRoot, "no-such-folder"));
                break;
            case "corrupt-image":
                File.WriteAllBytes(Path.Combine(images, "zz_corrupt.bmp"), Array.Empty<byte>());
                break;
            case "sensor-mismatch":
                var small = Directory.GetFiles(Path.Combine(RepoRoot, "TestImages"), "*.jpg").First();
                File.Copy(small, Path.Combine(images, "zz_small.jpg"));
                break;
            case "image-dir-is-file":
                File.WriteAllText(Path.Combine(runRoot, "InspectedImages"), "这个文件占住了存图目录的位置");
                break;
            case "recipe-corrupt":
                File.WriteAllText(recipePath, "{ 这不是 JSON  ");
                break;
            case "fast-tick":
                Set(settings, "Trigger", "TimerIntervalMs", 50);
                runSeconds = 6.0;
                break;
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

        var result = new Dictionary<string, object?> { ["scenario"] = name };
        var host = new AppHost();
        var sw = Stopwatch.StartNew();

        try
        {
            host.Configure(runRoot);
        }
        catch (Exception ex)
        {
            result["startupError"] = $"{ex.GetType().Name}: {ex.Message}";
            Console.WriteLine(JsonSerializer.Serialize(result));
            return 2;
        }

        using var memCts = new CancellationTokenSource();
        var memTask = SampleMemoryAsync(memCts.Token);
        try
        {
            await host.InitializeAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            result["startupError"] = $"{ex.GetType().Name}: {ex.Message}";
            Console.WriteLine(JsonSerializer.Serialize(result));
            return 2;
        }

        result["initMs"] = sw.ElapsedMilliseconds;

        var trigger = host.Services.GetRequiredService<ITriggerSource>();
        var repo = host.Services.GetRequiredService<IInspectionRepository>();
        var devices = host.Services.GetRequiredService<DeviceManager>();
        var (online, total) = devices.ConnectionStats;
        result["devices"] = $"{online}/{total}";
        result["healthFail"] = string.Join(";", host.HealthResults.Where(h => !h.OK).Select(h => h.Item));

        // 触发计数：TimerTrigger 实际发了多少次（与"跑完多少帧"对比，就能看出丢帧）
        trigger.Triggered += () => Interlocked.Increment(ref _fired);

        SqliteConnection? locker = null;
        if (name == "db-locked")
        {
            locker = new SqliteConnection($"Data Source={Path.Combine(runRoot, "visionstation.db")}");
            locker.Open();
            using var pragma = locker.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout=0;";
            pragma.ExecuteNonQuery();
            using var tx = locker.CreateCommand();
            tx.CommandText = "BEGIN EXCLUSIVE;";
            tx.ExecuteNonQuery();
            result["dbLocked"] = true;
        }

        trigger.Start();
        await Task.Delay(TimeSpan.FromSeconds(runSeconds));
        trigger.Stop();
        await Task.Delay(800);           // 给在途帧收尾

        if (locker is not null)
        {
            try
            {
                using var cmd = locker.CreateCommand();
                cmd.CommandText = "COMMIT;";
                cmd.ExecuteNonQuery();
            }
            catch { /* 锁释放失败不影响结论 */ }
            await locker.DisposeAsync();
        }

        memCts.Cancel();
        await memTask;
        result["fired"] = Interlocked.Read(ref _fired);
        result["peakMB"] = Math.Round(_peakBytes / 1024.0 / 1024.0, 1);

        try
        {
            var (ok, ng) = await repo.GetStatisticsAsync();
            result["dbOk"] = ok;
            result["dbNg"] = ng;
        }
        catch (Exception ex)
        {
            result["dbError"] = $"{ex.GetType().Name}: {ex.Message}";
        }

        var inspected = Path.Combine(runRoot, "InspectedImages");
        result["images"] = Directory.Exists(inspected)
            ? Directory.GetFiles(inspected, "*", SearchOption.AllDirectories).Length
            : 0;

        var log = Directory.Exists(Path.Combine(runRoot, "logs"))
            ? Directory.GetFiles(Path.Combine(runRoot, "logs"), "*.log").FirstOrDefault()
            : null;
        if (log is not null)
        {
            // 日志文件此刻仍被 Serilog 打开着，必须按共享方式读，否则 IOException。
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var text = reader.ReadToEnd();
            // 用"耗时:"结尾标记数真实跑完的帧：[DB] 那行里也含"判定:"，会重复计数。
            result["judged"] = Count(text, "耗时:");
            result["dbRows"] = Count(text, "[DB] 保存记录");
            result["cameraFail"] = Count(text, "采图失败");
            result["saveFail"] = Count(text, "[存图失败]");
            result["recipeWarn"] = Count(text, "[配方]");
            result["logErrors"] = text.Split('\n').Count(l => l.Contains("[ERR]"));
            result["dropWarn"] = Count(text, "上一帧未完成");
        }

        await host.StopAsync(TimeSpan.FromSeconds(8));
        await Serilog.Log.CloseAndFlushAsync();

        Console.WriteLine(JsonSerializer.Serialize(result));
        return 0;
    }

    private static void Set(Dictionary<string, object?> s, string section, string key, object value)
        => ((Dictionary<string, object?>)s[section]!)[key] = value;

    private static int Count(string text, string marker)
    {
        var n = 0;
        var i = 0;
        while ((i = text.IndexOf(marker, i, StringComparison.Ordinal)) >= 0) { n++; i += marker.Length; }
        return n;
    }

    private static async Task SampleMemoryAsync(CancellationToken ct)
    {
        var p = Process.GetCurrentProcess();
        while (!ct.IsCancellationRequested)
        {
            p.Refresh();
            var v = p.PrivateMemorySize64;
            if (v > Interlocked.Read(ref _peakBytes)) Interlocked.Exchange(ref _peakBytes, v);
            try { await Task.Delay(250, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            try { Directory.Delete(path, true); }
            catch (IOException) { /* 上次残留的 db 句柄没放，改名绕开 */ }
        }
        Directory.CreateDirectory(path);
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
    }
}
