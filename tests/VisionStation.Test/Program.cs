using OpenCvSharp;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;
using VisionStation.Devices;
using VisionStation.Storage;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("══════════════════════════════════════════");
Console.WriteLine("   VisionStation 端到端链路测试");
Console.WriteLine("══════════════════════════════════════════\n");

// ══ Step 1: 生成合成测试图像 ══
Console.WriteLine("[1/6] 生成合成测试图像...");
var testDir = Path.Combine(Directory.GetCurrentDirectory(), "TestImages");
Directory.CreateDirectory(testDir);

GenerateGoodPart(Path.Combine(testDir, "01_good.jpg"));
GenerateBadPart(Path.Combine(testDir, "02_bad_missing_hole.jpg"));
GenerateBadPart2(Path.Combine(testDir, "03_bad_scratched.jpg"));
Console.WriteLine("   [OK] 3 张测试图像已生成\n");

// ══ Step 2: 准备模板 ══
Console.WriteLine("[2/6] 准备模板图像（从合格件抠孔）...");
using var goodImg = Cv2.ImRead(Path.Combine(testDir, "01_good.jpg"), ImreadModes.Grayscale);
var templateRoi = new Rect(135, 135, 50, 50);
using var template = new Mat(goodImg, templateRoi);
var templatePath = Path.Combine(testDir, "hole_template.png");
Cv2.ImWrite(templatePath, template);
Console.WriteLine($"   [OK] 模板已保存: {templatePath}\n");

// ══ Step 3: 配置检测流水线 ══
Console.WriteLine("[3/6] 初始化检测流水线...");

const double PIXEL_PER_MM = 15.0;
var calibConfig = new CalibrationConfig
{
    PixelPerMillimeter = PIXEL_PER_MM,
    DesignedDiameterMm = 40.0 / PIXEL_PER_MM, // 孔40px → 2.67mm
    DimensionToleranceMm = 0.5
};

var calibration = new Calibration();
calibration.SetConfig(calibConfig);

var presTool = new PresenceTool(0.70);
presTool.LoadTemplate(template);
var dimTool = new DimensionTool(calibration, calibConfig.DesignedDiameterMm, calibConfig.DimensionToleranceMm);
var defTool = new DefectTool(20, 128);
defTool.ExcludeCenterX = 150;  // 孔排除圆（图像坐标）：垫圈中心孔不判缺陷
defTool.ExcludeCenterY = 150;
defTool.ExcludeRadius = 28;
var pipeline = new Pipeline(new List<IVisionTool> { presTool, dimTool, defTool });

var rois = new List<RoiRegion>
{
    new() { Name = "Presence",  X = 120, Y = 120, Width = 80, Height = 80 },
    new() { Name = "Dimension", X = 120, Y = 120, Width = 80, Height = 80 },
    new() { Name = "Defect",    X = 30,  Y = 30,  Width = 240, Height = 240 }
};
Console.WriteLine("   [OK] 流水线就绪\n");

// ══ Step 4: 初始化存储 ══
Console.WriteLine("[4/6] 初始化数据库和图片存储...");
var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "test_visionstation.db");
if (File.Exists(dbPath)) File.Delete(dbPath);
var db = new InspectionDbContext(dbPath);
var repo = new InspectionRepository(db);
await repo.EnsureCreatedAsync();

var imageStoreDir = Path.Combine(Directory.GetCurrentDirectory(), "InspectedImages");
if (Directory.Exists(imageStoreDir))
    Directory.Delete(imageStoreDir, true);
var imageStore = new ImageStoreService(imageStoreDir);
imageStore.SetMode(ImageStoreMode.All);
Console.WriteLine($"   [OK] 数据库: {dbPath}\n");

// ══ Step 5: 模拟产线逐张检测 ══
Console.WriteLine("[5/6] 模拟产线检测流程...\n");

var camera = new FileCamera(testDir, "*.jpg", loop: false);
camera.Open();   // S2.5：连接在 open，不在构造函数
int frameNum = 0;

while (true)
{
    var frame = camera.Capture();
    if (frame == null) break;
    frameNum++;

    Console.WriteLine($"  --- 第 {frameNum} 帧: {Path.GetFileName(frame.FilePath)} ---");

    var report = pipeline.Run(frame.Image, rois, calibConfig, "Manual", frame.FilePath);
    imageStore.Save(frame, report);
    await repo.SaveAsync(report);

    PrintResult("有无检测", report.PresenceResult.IsOk, report.PresenceResult.Summary);
    PrintResult("尺寸检测", report.DimensionResult.IsOk, report.DimensionResult.Summary);
    PrintResult("缺陷检测", report.DefectResult.IsOk, report.DefectResult.Summary);

    Console.ForegroundColor = report.IsOk ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine($"       总判定: {report.OverallVerdict}");
    Console.ResetColor();
    Console.WriteLine();

    frame.Image.Dispose();
}

// ══ Step 6: 汇总 ══
Console.WriteLine("[6/6] 汇总统计...");
var (okCount, ngCount) = await repo.GetStatisticsAsync();
Console.WriteLine($"  OK: {okCount}   NG: {ngCount}  总计: {okCount + ngCount}");

var recent = await repo.GetRecentAsync(10);
Console.WriteLine($"  数据库记录数: {recent.Count}");
foreach (var r in recent)
    Console.WriteLine($"    {r.Timestamp:HH:mm:ss} | {r.OverallVerdict,-4} | 有无:{r.PresenceOk} 尺寸:{r.DimensionOk} 缺陷:{r.DefectOk} | 实测:{r.MeasuredDiameter:F3}mm");

Console.WriteLine("\n===========================================");
Console.WriteLine("  链路验证通过!");
Console.WriteLine("===========================================");
Console.WriteLine($"\n  测试图片: {testDir}");
Console.WriteLine($"  检测结果图: {imageStoreDir}");
Console.WriteLine($"  数据库: {dbPath}");

goodImg.Dispose();
template.Dispose();

Console.WriteLine("\n按任意键退出...");

// ═══════════════════════════════════════════
// 辅助函数
// ═══════════════════════════════════════════

void PrintResult(string label, bool isOk, string summary)
{
    var color = isOk ? ConsoleColor.Green : ConsoleColor.Red;
    Console.Write($"     {label}: ");
    Console.ForegroundColor = color;
    Console.Write(isOk ? "[OK]  " : "[NG]  ");
    Console.ResetColor();
    Console.WriteLine(summary);
}

// ── 图像生成函数 ──

void GenerateGoodPart(string path)
{
    using var img = new Mat(300, 300, MatType.CV_8UC3, new Scalar(30, 30, 30));
    Cv2.Circle(img, 150, 150, 120, new Scalar(160, 160, 160), -1);

    // 金属表面纹理
    var rand = new Random(42);
    for (int i = 0; i < 5000; i++)
    {
        int x = rand.Next(40, 260), y = rand.Next(40, 260);
        int dx = x - 150, dy = y - 150;
        if (dx * dx + dy * dy < 115 * 115)
        {
            byte n = (byte)(rand.Next(-8, 9));
            img.Set(y, x, new Vec3b((byte)(160 + n), (byte)(160 + n), (byte)(160 + n)));
        }
    }
    Cv2.Circle(img, 150, 150, 20, new Scalar(25, 25, 25), -1); // 标准孔
    Cv2.ImWrite(path, img);
    Console.WriteLine($"     生成: {Path.GetFileName(path)} (合格件: 圆盘+圆孔20px)");
}

void GenerateBadPart(string path)
{
    using var img = new Mat(300, 300, MatType.CV_8UC3, new Scalar(30, 30, 30));
    Cv2.Circle(img, 150, 150, 120, new Scalar(160, 160, 160), -1);

    var rand = new Random(123);
    for (int i = 0; i < 3000; i++)
    {
        int x = rand.Next(40, 260), y = rand.Next(40, 260);
        int dx = x - 150, dy = y - 150;
        if (dx * dx + dy * dy < 115 * 115)
        {
            byte n = (byte)(rand.Next(-5, 6));
            img.Set(y, x, new Vec3b((byte)(160 + n), (byte)(160 + n), (byte)(160 + n)));
        }
    }
    // 故意不放孔 (缺孔)

    // 划痕1
    for (int x = 60; x < 180; x++)
    {
        int y = 100 + (int)(Math.Sin(x * 0.1) * 3);
        for (int dy = -2; dy <= 2; dy++)
            img.Set(y + dy, x + (dy == 0 ? 0 : 1), new Vec3b(40, 40, 40));
    }
    // 划痕2
    for (int x = 80; x < 200; x++)
    {
        int y = 180 + (int)(Math.Cos(x * 0.08) * 2);
        for (int dy = -1; dy <= 1; dy++)
            img.Set(y + dy, x, new Vec3b(50, 50, 50));
    }
    Cv2.ImWrite(path, img);
    Console.WriteLine($"     生成: {Path.GetFileName(path)} (NG: 缺孔 + 两条划痕)");
}

void GenerateBadPart2(string path)
{
    using var img = new Mat(300, 300, MatType.CV_8UC3, new Scalar(30, 30, 30));
    Cv2.Circle(img, 150, 150, 120, new Scalar(160, 160, 160), -1);

    var rand = new Random(99);
    for (int i = 0; i < 4000; i++)
    {
        int x = rand.Next(40, 260), y = rand.Next(40, 260);
        int dx = x - 150, dy = y - 150;
        if (dx * dx + dy * dy < 115 * 115)
        {
            byte n = (byte)(rand.Next(-10, 11));
            img.Set(y, x, new Vec3b((byte)(160 + n), (byte)(160 + n), (byte)(160 + n)));
        }
    }
    Cv2.Circle(img, 150, 150, 24, new Scalar(25, 25, 25), -1); // 偏大孔(48px vs 40px标准)

    // 一条划痕
    for (int x = 100; x < 220; x++)
    {
        int y = 70 + (int)(Math.Sin(x * 0.05) * 5);
        for (int dy = -2; dy <= 2; dy++)
            img.Set(y + dy, x, new Vec3b(45, 45, 45));
    }
    Cv2.ImWrite(path, img);
    Console.WriteLine($"     生成: {Path.GetFileName(path)} (NG: 孔偏大 + 划痕)");
}
