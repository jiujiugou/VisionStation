using VisionStation.Devices;
using VisionStation.Service;

// ═══════════════════════════════════════════════════════════════
// V-001 复现：HealthChecker 的数据库自检判据是否恒真
//
// 场景：dbPath 指向一个必然不存在的文件 + 必然不存在的父目录。
//       模板路径为空，配方为 null（制造最坏的合法输入组合）。
// 预期（约束 R-001）：数据库项必须判 FAIL。
// 实际：判据是 File.Exists(db) || Directory.Exists(父目录)，
//       但这里父目录也不存在 —— 所以还需第二个用例证明「恒真」：
//       父目录换成一定存在的目录（程序自身目录），文件保持不存在。
// ═══════════════════════════════════════════════════════════════

var evidencePath = args.Length > 0 ? args[0] : "evidence_V001.txt";
var lines = new List<string>
{
    $"# V-001 复现证据  {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
    $"# 预期（R-001）：两例均应判 FAIL；实际见下"
};

void Run(string label, string dbPath)
{
    var hc = new HealthChecker(
        new DeviceManager(),
        dbPath,
        Path.Combine(Path.GetTempPath(), "vs_repro_storage_" + Guid.NewGuid().ToString("N")[..8]),
        "",
        null);
    var db = hc.RunAll().First(r => r.Item == "数据库");
    lines.Add($"[{label}] dbPath={dbPath}");
    lines.Add($"[{label}] 自检结论: OK={db.OK}  Detail={db.Detail}  Error={db.Error ?? "无"}");
}

// 用例 1：文件和父目录都不存在
Run("用例1 文件+父目录均不存在", @"C:\__vs_repro_missing__\visionstation.db");
// 用例 2：文件不存在，但父目录必然存在（Windows 目录恒在）
Run("用例2 仅文件不存在", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "visionstation.db"));

File.WriteAllLines(evidencePath, lines);
Console.WriteLine(string.Join(Environment.NewLine, lines));
