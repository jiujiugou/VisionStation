using System.Diagnostics;
using System.Text.Json;

// VisionStation 看门狗 —— 常驻单实例守护主进程，崩了自动拉起。
// 由 VisionStation.App 启动时创建，操作员无感知。
//
// 三条硬规则（旧实现全都违反了）：
//   1. 只在"异常终止"时重启：主进程正常关闭会 Set 一个命名事件，看门狗据此判定；
//      不用退出码 —— GetProcessById 拿到的进程对象读 ExitCode 不可靠（实测拿不到值）。
//   2. 崩溃循环保护：窗口期内重启次数超限就不再拉起，写 CRITICAL 等人工介入，
//      杜绝"配方坏 / 模型缺失"这类确定性崩溃 → 拉起 → 再崩的无限抖动。
//   3. 单实例：互斥量保证同一时刻只有一个看门狗在守护（主进程每次启动都会拉起一个）。

const string shutdownEventName = "VisionStation.Shutdown";
const string singletonMutexName = "VisionStation.Watchdog";

var parentPid = args.Length > 0 ? int.Parse(args[0]) : 0;
var exePath = args.Length > 1 ? args[1] : "VisionStation.App.exe";
if (parentPid == 0) return;

// 单实例守卫：抢不到锁说明已有看门狗在守护（主进程重启会带一个新的看门狗，那个直接退场）。
// 用 WaitOne(0) 而非 initiallyOwned：上一个看门狗被强杀后锁可能处于"被遗弃"状态，
// 此时 WaitOne 抛 AbandonedMutexException —— 等于把锁判给我们，按已获得处理。
using var singleton = new Mutex(initiallyOwned: false, singletonMutexName);
bool acquired;
try { acquired = singleton.WaitOne(0); }
catch (AbandonedMutexException) { acquired = true; }
if (!acquired) return;

// 正常关闭信号：由主进程在 OnExit 时 Set。拿不到就退回纯崩溃判定。
EventWaitHandle? shutdown = null;
try { shutdown = EventWaitHandle.OpenExisting(shutdownEventName); }
catch (WaitHandleCannotBeOpenedException) { /* 主进程未提供关闭信号 */ }

var cfg = WatchdogConfig.Load();
var logPath = Path.Combine(AppContext.BaseDirectory, "watchdog.log");
var history = new Queue<DateTime>();

Process? parent = null;
try { parent = Process.GetProcessById(parentPid); }
catch (ArgumentException) { /* 主进程已经不在了，按异常处理 */ }

while (true)
{
    if (WaitForExit(parent, shutdown))
    {
        Log("收到正常关闭信号，看门狗结束");
        return;
    }
    Log("主进程异常终止");

    // 崩溃循环保护：只统计窗口期内的重启次数
    var now = DateTime.Now;
    history.Enqueue(now);
    while (history.Count > 0 && (now - history.Peek()).TotalMinutes > cfg.WindowMinutes)
        history.Dequeue();

    if (history.Count >= cfg.MaxRestarts)
    {
        Log($"CRITICAL: {cfg.WindowMinutes} 分钟内已重启 {history.Count} 次，停止自动拉起，需人工介入");
        return;
    }

    // 指数退避：2s → 4s → 8s …（封顶），给瞬时故障自愈的机会，又不至于疯狂刷屏
    var delay = (int)Math.Min(cfg.RestartDelaySeconds * Math.Pow(2, history.Count - 1), cfg.BackoffMaxSeconds);
    Log($"{delay} 秒后重启...");
    Thread.Sleep(TimeSpan.FromSeconds(delay));

    try
    {
        parent = Process.Start(new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        });
        Log($"主进程已重启 (PID={parent?.Id})");
    }
    catch (Exception ex)
    {
        Log($"重启失败: {ex.Message}");
        return;
    }
}

// 等待主进程退出；返回 true 表示收到正常关闭信号（不该重启），false 表示异常终止
static bool WaitForExit(Process? parent, EventWaitHandle? shutdown)
{
    if (parent == null) return false;

    while (true)
    {
        if (shutdown != null && shutdown.WaitOne(0))
            return true;

        if (parent.HasExited)
        {
            // 进程已退出：再给关闭信号一个很短的观察窗口，避免"先 Set 后退出"的时序竞争
            return shutdown != null && shutdown.WaitOne(200);
        }

        if (shutdown != null) shutdown.WaitOne(500);
        else Thread.Sleep(500);
    }
}

void Log(string msg)
{
    try { File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n"); }
    catch { /* 日志写不了不能反过来把看门狗搞崩 */ }
}

/// <summary>看门狗策略：从 exe 同目录的 appsettings.json 的 Watchdog 节读取，缺失用默认值</summary>
sealed record WatchdogConfig(int MaxRestarts, double WindowMinutes, int RestartDelaySeconds, int BackoffMaxSeconds)
{
    public static WatchdogConfig Load()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
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
        return new WatchdogConfig(3, 10, 2, 60);
    }

    private static int GetInt(JsonElement e, string name, int def)
        => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : def;
}
