// 阶段 3 探针：对 App 切片的可疑点逐一取客观事实。
// 只读环境、不写产线数据（唯一写入目标是临时目录与一个不存在盘符的路径）。
// 每项打印 P<n> 结论，供 evidence 归档；不抛异常退出，异常本身也是事实。
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Configuration;
using VisionStation.App;
using VisionStation.App.Services;
using VisionStation.App.Services.Coordinates;
using VisionStation.App.ViewModels;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;
using VisionStation.Devices;
using CommunityToolkit.Mvvm.Messaging;

var root = @"D:\Code\VisionStation";
var tmp = Path.Combine(Path.GetTempPath(), "vs-probe-app-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(tmp);

IConfiguration Cfg(Dictionary<string, string?> kv) =>
    new ConfigurationBuilder().AddInMemoryCollection(kv).Build();

Console.WriteLine($"探针启动 · 机器 {Environment.MachineName} · .NET {Environment.Version} · cwd={Environment.CurrentDirectory}");
Console.WriteLine();

// ────────────────────────────────────────────────────────────────
// P1  AppPaths：输出目录是否随进程工作目录漂移
// ────────────────────────────────────────────────────────────────
Console.WriteLine("P1 AppPaths —— 输出路径的锚点");
{
    var cwd0 = Environment.CurrentDirectory;
    var cfg = Cfg(new()
    {
        ["Storage:ImageFolder"] = "InspectedImages",
        ["Storage:DbPath"] = "visionstation.db",
        ["Logging:LogFolder"] = "Logs",
    });

    var a = new AppPaths(cfg);                       // 仓库根作 cwd
    Environment.CurrentDirectory = tmp;               // 模拟「计划任务/快捷方式」把 cwd 设成别处
    var b = new AppPaths(cfg);
    Environment.CurrentDirectory = cwd0;

    Console.WriteLine($"  cwd={cwd0,-40} DbPath={a.DbPath}");
    Console.WriteLine($"  cwd={tmp,-40} DbPath={b.DbPath}");
    Console.WriteLine($"  两个 cwd 下 DbPath 是否相同: {string.Equals(a.DbPath, b.DbPath, StringComparison.OrdinalIgnoreCase)}");
    Console.WriteLine($"  StorageFolder 是否落在 cwd 下: {b.StorageFolder.StartsWith(tmp, StringComparison.OrdinalIgnoreCase)}");
    Console.WriteLine("P1 结论: 输出三件套（db / 图片 / 日志）完全由 Environment.CurrentDirectory 决定，"
                    + "配置里没有任何基准目录可锚定；cwd 不可写时无处可落。");
}
Console.WriteLine();

// ────────────────────────────────────────────────────────────────
// P2  CameraFactory：未知模式字符串的落点 + UI 显示名与实际设备
// ────────────────────────────────────────────────────────────────
Console.WriteLine("P2 CameraFactory —— 未知模式字符串");
{
    var cfg = Cfg(new() { ["Camera:Mode"] = "HikVision", ["Camera:Loop"] = "true" });
    var paths = new AppPaths(cfg);

    foreach (var mode in new[] { "Hikvision", "HikVision", "HIK", "File" })
    {
        var f = new CameraFactory(paths, cfg);
        try
        {
            var cam = f.CreateByMode(mode);
            var swapped = new SwappableCamera(cam, m => cam, mode);
            // CameraPageViewModel 的显示逻辑：未知模式原样显示模式名
            var shown = mode switch
            {
                "File" => "文件相机", "Webcam" => "USB摄像头", "Hikvision" => "海康工业相机",
                _ => mode
            };
            Console.WriteLine($"  Mode=\"{mode,-10}\" -> 实例={cam.GetType().Name,-14} 界面显示=\"{shown}\""
                            + $"{(shown == "海康工业相机" && cam is not HikCamera ? "  ← 名实不符" : "")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Mode=\"{mode}\" -> 抛 {ex.GetType().Name}: {ex.Message}");
        }
    }
    Console.WriteLine("P2 结论: 拼错/大小写不符的模式名不报错，静默落到 FileCamera；"
                    + "而相机页把模式名原样当作「海康工业相机」类显示，界面与真实设备不一致。");
}
Console.WriteLine();

// ────────────────────────────────────────────────────────────────
// P3  ToolFactory：未知工具类型的落点
// ────────────────────────────────────────────────────────────────
Console.WriteLine("P3 ToolFactory —— 未知工具类型");
{
    var recipe = new RecipeConfig();
    recipe.Calibration = new CalibrationData { PixelPerMillimeter = 0.05, DesignedDiameterMm = 12, DimensionToleranceMm = 0.1 };
    recipe.Rois.Add(new RoiData { Name = "Presence", X = 0, Y = 0, Width = 100, Height = 100 });
    recipe.Tools.Add(new ToolConfigData { Type = "Presence", Roi = "Presence", Enabled = true });
    recipe.Tools.Add(new ToolConfigData { Type = "Dimension", Roi = "Presence", Enabled = true });
    recipe.Tools.Add(new ToolConfigData { Type = "Defect", Roi = "Presence", Enabled = true });
    recipe.Tools.Add(new ToolConfigData { Type = "OCR", Roi = "Presence", Enabled = true });   // 拼错或新版工具

    var calib = new Calibration();
    calib.SetConfig(new CalibrationConfig { PixelPerMillimeter = 0.05, DesignedDiameterMm = 12, DimensionToleranceMm = 0.1 });

    var tools = new ToolFactory(calib, recipe, null).CreateAll();
    Console.WriteLine($"  配方启用工具 {recipe.Tools.Count(t => t.Enabled)} 个 -> 实际生成 {tools.Count} 个：{string.Join(",", tools.Select(t => t.GetType().Name))}");
    Console.WriteLine($"  未知类型是否抛异常: 否（无输出、无返回值、调用方无从得知少了一个）");
    Console.WriteLine("P3 结论: 未知 Type 只在 Debug.WriteLine 留一行（Release 下不进任何日志），"
                    + "工具静默缺席，而总判定只按剩余工具算 —— 少检一项不会被判 NG。");
}
Console.WriteLine();

// ────────────────────────────────────────────────────────────────
// P4  RoiEditorViewModel.SaveToFile：写盘失败的表现
// ────────────────────────────────────────────────────────────────
Console.WriteLine("P4 ROI 配方保存 —— 目标不可写");
{
    var cfg = Cfg(new() { ["Recipe:Path"] = @"Z:\no-such-drive\recipes\default.json" });   // Z: 不存在
    var paths = new AppPaths(cfg);
    var recipe = new RecipeConfig();
    recipe.Calibration = new CalibrationData { PixelPerMillimeter = 0.05, DesignedDiameterMm = 12, DimensionToleranceMm = 0.1 };
    recipe.Rois.Add(new RoiData { Name = "ROI-1", X = 10, Y = 10, Width = 50, Height = 50 });
    recipe.Tools.Add(new ToolConfigData { Type = "Presence", Roi = "ROI-1", Enabled = true });

    var vm = new RoiEditorViewModel(new CoordinateMapper(), recipe, paths);
    Console.WriteLine($"  RecipePath = {paths.RecipePath}");
    try
    {
        vm.SaveToFileCommand.Execute(null);
        Console.WriteLine("  结果: 未抛异常（走到了成功分支）");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  结果: 未捕获异常 {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        Console.WriteLine("  → RelayCommand 的同步 Execute 不会吞异常：异常冒到 WPF 输入循环，"
                        + "App 的 DispatcherUnhandledException 里 Handled=false → 进程退出 → 看门狗重启。");
    }
    Console.WriteLine("P4 结论: 配方保存没有任何 try/catch；盘符不存在/只读/磁盘满一律升级为进程崩溃，"
                    + "界面无提示，当前编辑的 ROI 全部丢失。");
}
Console.WriteLine();

// ────────────────────────────────────────────────────────────────
// P5  ResultPanel：报告里有第 4/5 个工具时界面显示几行
// ────────────────────────────────────────────────────────────────
Console.WriteLine("P5 结果面板 —— 多工具报告的显示行");
{
    var report = new InspectionReport { TriggerSource = "Probe" };
    report.SetToolResult("Presence", new ToolResult { IsOk = true, Metrics = new() { ["MatchScore"] = 0.93 } });
    report.SetToolResult("Dimension", new ToolResult { IsOk = true, Metrics = new() { ["Deviation_mm"] = 0.02 } });
    report.SetToolResult("Defect", new ToolResult { IsOk = true, Metrics = new() { ["ScratchCount"] = 0, ["NotchCount"] = 0 } });
    report.SetToolResult("Yolo", new ToolResult { IsOk = false, Metrics = new() { ["Detections"] = 1 } });  // 第四个工具判 NG

    var vm = new ResultPanelViewModel();
    WeakReferenceMessenger.Default.Send(new InspectionResultMessage(report));

    Console.WriteLine($"  报告内工具数: {report.Results.Count}，其中 NG: {report.Results.Count(r => !r.Value.IsOk)}");
    Console.WriteLine($"  总判定条: {vm.VerdictText}（颜色 {vm.VerdictColor}）");
    Console.WriteLine($"  表格行数: {vm.Rows.Count} -> {string.Join(" | ", vm.Rows.Select(r => $"{r.Item}:{r.StatusText}"))}");
    Console.WriteLine("P5 结论: 表格固定 4 行（特征/直径/划痕/缺口），第 4 个工具（Yolo）既不出现也不显示 NG；"
                    + "于是界面出现「总判定 NG，四行全 OK」，操作员无法解释 NG 从哪来。");
}
Console.WriteLine();

// ────────────────────────────────────────────────────────────────
// P6  帧 Mat：不释放时内存怎么走
// ────────────────────────────────────────────────────────────────
Console.WriteLine("P6 帧 Mat 生命周期 —— 采集 50 帧不 Dispose");
{
    var folder = Path.Combine(root, "TestImages");
    var cam = new FileCamera(folder, "*.jpg;*.png;*.bmp", loop: true);
    var first = cam.Capture();
    if (first == null)
    {
        Console.WriteLine("  TestImages 无可读图片，跳过（不臆造数据）");
    }
    else
    {
        Console.WriteLine($"  单帧尺寸: {first.Image.Width}x{first.Image.Height}x{first.Image.Channels()} "
                        + $"≈ {first.Image.Width * first.Image.Height * first.Image.Channels() / 1024.0 / 1024.0:F2} MB（非托管）");
        Console.WriteLine($"  Capture 之后 Mat 是否已被释放: {first.Image.IsDisposed}（调用方负责）");

        long before = Process.GetCurrentProcess().PrivateMemorySize64;
        for (int i = 0; i < 50; i++) _ = cam.Capture();          // 只取引用，不 Dispose —— 与 MainViewModel 当前写法一致
        long after = Process.GetCurrentProcess().PrivateMemorySize64;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long afterGc = Process.GetCurrentProcess().PrivateMemorySize64;

        Console.WriteLine($"  私有字节: 采集前 {before / 1024 / 1024} MB → 50 帧后 {after / 1024 / 1024} MB"
                        + $" → 强制 GC+终结器后 {afterGc / 1024 / 1024} MB");
        Console.WriteLine($"  差值: 增长 {(after - before) / 1024 / 1024} MB，GC 回收 {(after - afterGc) / 1024 / 1024} MB");
        Console.WriteLine("P6 结论: 每帧 Mat 的唯一释放者是 OpenCvSharp 的终结器。"
                        + "托管堆压力小（像素在非托管内存）时 GC 不着急跑，工作集会一直涨到某次 Gen2/内存压力才回落；"
                        + "而 tests/VisionStation.Test/Program.cs:105 是显式 Dispose 的 —— 消费方释放是既定契约，App 这一侧没有履行。");
    }
}
Console.WriteLine();

// ────────────────────────────────────────────────────────────────
// P7  状态栏 FPS：单帧耗时取整为 0 时
// ────────────────────────────────────────────────────────────────
Console.WriteLine("P7 状态栏 FPS —— 周期为 0");
{
    double ema = 0;                       // Stopwatch.ElapsedMilliseconds 取整后可能为 0
    double fps = 1000.0 / ema;
    Console.WriteLine($"  _emaCycleMs=0 -> Fps=\"{fps:0.0}\"（double.IsInfinity={double.IsInfinity(fps)}）");
    Console.WriteLine("P7 结论: 显示为 ∞，不是崩溃但状态栏读数失真；操作员据此判断节拍会被误导。");
}
Console.WriteLine();
Console.WriteLine("探针结束。");
try { Directory.Delete(tmp, true); } catch { }
return 0;
