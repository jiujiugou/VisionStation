using System.Diagnostics;
using VisionStation.Watchdog;

// VisionStation 看门狗 —— 常驻单实例守护主进程，崩了自动拉起。
// 由 VisionStation.App 启动时创建，操作员无感知。
//
// 三条硬规则（旧实现全都违反了）：
//   1. 只在"异常终止"时重启：主进程正常关闭会 Set 一个命名事件，看门狗据此判定；
//      不用退出码 —— GetProcessById 拿到的进程对象读 ExitCode 不可靠（实测拿不到值）。
//   2. 崩溃循环保护：窗口期内重启次数超限就不再拉起，写 CRITICAL 等人工介入，
//      杜绝"配方坏 / 模型缺失"这类确定性崩溃 → 拉起 → 再崩的无限抖动。
//   3. 单实例：互斥量保证同一时刻只有一个看门狗在守护（主进程每次启动都会拉起一个）。
//
// ─────────────────────────────────────────────────────────────────────────────
// 为什么必须是**独立进程**，而不是主程序里的一个线程：
//   无人值守时最需要守护的故障恰恰是"主程序自己出问题"——UI 线程死锁、未处理异常、
//   内存打爆被系统杀掉、甚至整个进程被任务管理器结束。这些情况下进程内的守护线程
//   和受害者一起消失，监控等于不存在。独立进程是唯一能在"被守护者已经不响应"时
//   还活着的形态；代价是它只能看到进程级信号（见下面的监控口径），看不到业务状态。
//
// 监控口径（按代码写实，别按名字想当然）：
//   只看**进程存在性** —— Process.HasExited。没有心跳文件、没有窗口响应探测、
//   没有"卡住多久算死"的判定。所以"主程序卡死但进程还在"（典型：模态框卡住 Dispatcher、
//   死锁）看门狗是发现不了的，它只会一直等。真要覆盖那种场景，得加心跳文件 + 超时，
//   并且先解决"怎么区分正常忙和真卡死"，否则会误杀正在跑长任务的实例。
//
// 正常退出的信号链路（跨进程，靠命名内核对象，不靠参数/文件）：
//   主进程 App.StartWatchdog 先创建命名事件 "VisionStation.Shutdown"（ManualReset）并 Reset，
//   然后带参数启动本进程：<主进程 PID> "<主程序 exe 全路径>" [主程序原始命令行…]。
//   正常关窗收尾完成后，AppHost.StopAsync 第 6 步 → App.NotifyWatchdogCleanExit → 事件 Set；
//   App.OnExit 里还有一次兜底 Set（仅在启动走完时）。本进程收到 Set 就认为"这是正常退出"，
//   记一行日志然后退出，**不重启**。启动期崩溃不发这个信号 —— 那种情况要留给看门狗重试。
//
// 启动参数：argv[0] = 要守护的进程 PID；argv[1] = 重启时用的 exe 路径（可选，缺省用文件名）；
//   argv[2..] = **主程序的原始命令行参数**，崩溃重启时逐字重放（归属规则见 TakeMainArgs 的注释）。
//   为什么要重放：--data-root 决定数据根（db / 日志 / 检测图片都落在它下面）。丢了它，被拉起来的
//   新进程会回落成"数据根 = exe 所在目录"，现场表现为"崩溃重启之后数据丢了"—— 数据其实还在
//   原来那块盘上，只是新进程在看另一个空目录。它不报错、不崩溃、日志里也没有异常，
//   是现场最难排查的一类问题，所以这里宁可多带一个参数，也不能"重启成默认配置"。
//
// 日志：<看门狗 exe 目录>\watchdog.log（追加写，不滚动、不轮转；一行一条）。
// 退出码：本程序所有 return 都不带值 → 恒为 0，**不区分**"正常结束 / 放弃重启 / 重启失败"。
//   排障请看 watchdog.log 的最后一行，不要靠 $LASTEXITCODE 判断。
// ─────────────────────────────────────────────────────────────────────────────

// 这两个名字是与主进程的跨进程契约，必须与 App.xaml.cs 里的字面量逐字一致。
// 改任何一边都会变成"主程序 Set 的事件没人听"，而且不会报错 —— 只会表现为"每次关窗后都被重启一次"。
const string shutdownEventName = "VisionStation.Shutdown";
const string singletonMutexName = "VisionStation.Watchdog";

// 参数解析。PID 解析失败会直接抛 FormatException（没有 try）——
// 这只会发生在人手敲错参数的情况下，而那种情况**不守护比守护错对象好**：
// 猜一个 PID 去 Wait 是查不出问题的，宁可当场崩掉。
var parentPid = args.Length > 0 ? int.Parse(args[0]) : 0;
var exePath = args.Length > 1 ? args[1] : "VisionStation.App.exe";

// 主程序自己的参数（argv[2..]，切分规则见 WatchdogArgs）。只解析一次、之后每轮重启都用同一份 ——
// 重放的是"主程序当初被怎么启动的"，不是"看门狗现在想怎么启动它"。
var mainArgs = WatchdogArgs.TakeMainArgs(args);

// 没带 PID：说明不是主程序拉起来的（可能是有人双击了看门狗 exe）。
// 直接退出，不猜、不抱怨 —— 看门狗没有"独立运行"这种用法。
if (parentPid == 0) return;

// 单实例守卫：抢不到锁说明已有看门狗在守护（主进程重启会带一个新的看门狗，那个直接退场）。
// 用 WaitOne(0) 而非 initiallyOwned：上一个看门狗被强杀后锁可能处于"被遗弃"状态，
// 此时 WaitOne 抛 AbandonedMutexException —— 等于把锁判给我们，按已获得处理。
//
// using 让进程退出时释放互斥量：正常 return 会走 Dispose；被强杀则由内核标记为"被遗弃"，
// 下一个看门狗靠上面的异常分支接过来 —— 所以异常退出不会导致"看门狗永远起不来"。
using var singleton = new Mutex(initiallyOwned: false, singletonMutexName);
bool acquired;
try { acquired = singleton.WaitOne(0); }
catch (AbandonedMutexException) { acquired = true; }
if (!acquired) return;

// 正常关闭信号：由主进程在 OnExit 时 Set。拿不到就退回纯崩溃判定。
//
// 打开失败只有一种常见原因：主进程还没来得及创建（或已经退出并销毁）这个事件。
// 此时 shutdown 为 null，循环里只能靠"进程还在不在"判断 —— 后果是**正常关窗也会被当成崩溃重启一次**。
// 这不是崩溃，是信号链断了；日志里表现为"主进程异常终止"紧接着又起了新进程。
EventWaitHandle? shutdown = null;
try { shutdown = EventWaitHandle.OpenExisting(shutdownEventName); }
catch (WaitHandleCannotBeOpenedException) { /* 主进程未提供关闭信号 */ }

// 策略从主程序那份 appsettings.json 读：两个 exe 被部署在同一目录
// （App 的 CopyWatchdog 把本程序拷到输出目录），所以 AppContext.BaseDirectory 就是同一个目录。
var cfg = WatchdogConfig.Load();

// 日志固定写在 exe 旁边、追加模式。不滚动：平静运行时一天也就几行，
// 崩溃循环时一轮几行（那正是要看的内容）。不会自动清理 ——
// 增长足够慢，不值得为它引入轮转；真要归档，人工把文件挪走即可。
var logPath = Path.Combine(AppContext.BaseDirectory, "watchdog.log");

// 崩溃循环保护 + 退避的记账员（策略逻辑与测试见 RestartPolicy）。
var policy = new RestartPolicy(cfg);

// 拿主进程句柄。已经退出时 GetProcessById 抛 ArgumentException —— 这里当作"崩溃"继续往下走，
// 目的是让"主程序刚起来就崩"也能被拉起一次（而不是看门狗自己静默退出）。
Process? parent = null;
try { parent = Process.GetProcessById(parentPid); }
catch (ArgumentException) { /* 主进程已经不在了，按异常处理 */ }

// 守护循环：每轮 = 等一次主进程结束 → 判断该不该重启 → 重启后再等。
// 循环只在三种情况下退出：收到正常关闭信号、达到重启上限、重启本身失败。
while (true)
{
    if (WaitForExit(parent, shutdown))
    {
        // 正常关闭：主进程完成了收尾并 Set 了这个事件。看门狗就此收工 ——
        // 它不会被"下一次启动"复用，下次启动会拉一个新的看门狗。
        Log("收到正常关闭信号，看门狗结束");
        return;
    }
    Log("主进程异常终止");

    // 崩溃循环保护：只统计窗口期内的重启次数（记账时顺手清窗口外旧账）
    policy.Register(DateTime.Now);

    // 达到上限 → 停止尝试，留 CRITICAL 给人看。
    // 这一步是"确定性崩溃"（配方损坏、端口被占、依赖缺失）的唯一刹车：
    // 没有它，一台无人值守的机器会整夜"拉起 → 崩溃 → 拉起"，
    // 日志被刷满、磁盘被写满，第二天来看不出最初那条错误。
    if (policy.ShouldStop)
    {
        Log($"CRITICAL: {cfg.WindowMinutes} 分钟内已重启 {policy.WindowCount} 次，停止自动拉起，需人工介入");
        return;
    }

    // 指数退避：2s → 4s → 8s …（封顶），给瞬时故障自愈的机会，又不至于疯狂刷屏
    // 底数是配置里的 RestartDelaySeconds，指数是"窗口内第几次"（第 1 次 2^0=1 倍），
    // 封顶 BackoffMaxSeconds —— 没有封顶的话，第 6 次就是 64 秒、第 10 次接近 17 分钟。
    var delay = policy.NextDelaySeconds;
    Log($"{delay} 秒后重启...");

    // 用同步 Sleep 而不是定时器：看门狗整个进程是单线程的，这期间没有别的事要做。
    Thread.Sleep(TimeSpan.FromSeconds(delay));

    try
    {
        // UseShellExecute=false：不经过 shell，避免"以当前用户身份弹 UAC/再起一层进程"。
        // WorkingDirectory 显式设成 exe 目录：主程序若被配置成读相对路径（比如测试配置），
        // 从别处启动时 cwd 会是看门狗的目录，跟手动双击的结果不一致。
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };

        // 重放主程序原始命令行：参数就是它的运行配置（--data-root 决定数据根），
        // 丢掉参数等于"悄悄换了一套配置重启"，比直接起不来更难排查（见文件头说明）。
        //
        // 用 ArgumentList 而不是自己拼一个字符串：拼装交给框架，它按 Windows 的
        // CommandLineToArgvW 规则转义 —— 含空格的路径自动加引号，内嵌引号与结尾反斜杠
        // 按规则翻倍（"D:\My Data\VS\" 这种结尾反斜杠 + 引号的组合正是手拼最容易错的地方）。
        // 传进去的是**原始的、已经由上游拆好的一份参数**，这里既不做二次拆分也不做去引号，
        // 否则任何含空格的路径都会被拆成两半。
        foreach (var arg in mainArgs) psi.ArgumentList.Add(arg);

        parent = Process.Start(psi);
        Log($"主进程已重启 (PID={parent?.Id})" + (mainArgs.Length > 0
            ? $"，参数: {string.Join(' ', mainArgs)}"   // 排障最需要的那一行：这次是按什么配置拉起来的
            : "，无参数"));
    }
    catch (Exception ex)
    {
        // 起不来（exe 被移走 / 权限不足 / 杀软拦截）：记一条就退出。
        // 继续循环只会无限重试同一个必然失败的动作，而且退避时间再长也换不回 exe。
        Log($"重启失败: {ex.Message}");
        return;
    }
}

// 等待主进程退出；返回 true 表示收到正常关闭信号（不该重启），false 表示异常终止
//
// 实现是**轮询**（500ms 一次），不是 WaitHandle 合等：Process 没有能参与 WaitHandle.WaitAny
// 的公开句柄（WaitForExit / WaitForExitAsync 都会一直阻塞到进程结束，没法同时等那个事件），
// 而 500ms 的发现延迟对"崩了多久才拉起"完全可以接受。
// 这里没有任何超时上限 —— 主程序连跑三天就循环三天，每次只消耗一次 WaitOne(0)。
static bool WaitForExit(Process? parent, EventWaitHandle? shutdown)
{
    // 拿不到句柄（主程序在拉起看门狗前就没了）→ 直接按异常处理，交给上面的重启逻辑。
    if (parent == null) return false;

    while (true)
    {
        // 先看信号再看进程：正常关闭时主进程"先 Set、再退出"，
        // 如果反过来先判 HasExited，可能刚好错过"已经 Set 但还没退"的那个瞬间。
        if (shutdown != null && shutdown.WaitOne(0))
            return true;

        if (parent.HasExited)
        {
            // 进程已退出：再给关闭信号一个很短的观察窗口，避免"先 Set 后退出"的时序竞争
            //
            // 200ms 的选择：这里等的是"另一个进程写完一个内核对象"，不是磁盘 I/O。
            // 等太久会让每次"发现进程没了"的判定都慢一拍，等太短则会漏掉信号 →
            // 后果是把一次正常关窗当成崩溃、白重启一次（数据不会坏，但会吓人）。
            return shutdown != null && shutdown.WaitOne(200);
        }

        // 没有事件时纯 Sleep：有事件时用 WaitOne(500) 当"可被信号提前唤醒的 sleep"，
        // 让正常关闭能立刻被感知，而不是最多再等半秒。
        if (shutdown != null) shutdown.WaitOne(500);
        else Thread.Sleep(500);
    }
}

// 写一行日志（追加）。
// 吞掉所有异常是刻意的：日志写不了（目录只读、磁盘满、文件被占用）时，
// 看门狗仍必须继续守护 —— 不能因为"记不下来"就变成"不干活"。
// 单实例守卫保证同一时刻只有一个写者，所以这里不需要加锁（也不会有交错写入）。
void Log(string msg)
{
    try { File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n"); }
    catch { /* 日志写不了不能反过来把看门狗搞崩 */ }
}
