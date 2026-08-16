using System.Diagnostics;

// VisionStation 看门狗 —— 守护主进程，崩了自动拉起
// 由 VisionStation.App 在启动时自动创建，操作员无感知

var parentPid = args.Length > 0 ? int.Parse(args[0]) : 0;
var exePath = args.Length > 1 ? args[1] : "VisionStation.App.exe";

if (parentPid == 0) return;

File.WriteAllText("watchdog.log",
    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 看门狗启动 — 守护 PID:{parentPid}");

while (true)
{
    Thread.Sleep(3000);

    try
    {
        var parent = Process.GetProcessById(parentPid);
        if (parent.HasExited)
        {
            var t = DateTime.Now;
            File.AppendAllText("watchdog.log",
                $"\n[{t:yyyy-MM-dd HH:mm:ss}] 主进程已退出，2秒后重启...");

            Thread.Sleep(2000);
            Process.Start(exePath);
            File.AppendAllText("watchdog.log",
                $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 主进程已重新启动");
            return; // 新的主进程会启动自己的看门狗
        }
    }
    catch (ArgumentException)
    {
        File.AppendAllText("watchdog.log",
            $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 主进程未找到，拉起中...");
        Process.Start(exePath);
        return;
    }
    catch (Exception ex)
    {
        File.AppendAllText("watchdog.log",
            $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 异常: {ex.Message}");
    }
}
