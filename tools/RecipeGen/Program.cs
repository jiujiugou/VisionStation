// 配方生成器：.sol 方案包 → 配方 JSON 草稿。
// 用法：RecipeGen <方案.sol> [--out 输出.json] [--sensor 2448x2048]
//
// 能提取的（VmServer.xml 明文）：方案名、流程名、模块名册（含显示名）。
// 不能提取的（二进制里，绝不瞎编）：报文订阅顺序（按模块清单序预填，必须人工核对）、
//   语义角色（默认 Defect）、传感器分辨率（留 0 占位，或用 --sensor 提供）。
// 生成物是"草稿"不是"成品"——程序末尾会把需要人工确认的清单打出来。
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Xml.Linq;

if (args.Length < 1 || args[0] is "-h" or "--help")
{
    Console.WriteLine("用法: RecipeGen <方案.sol> [--out 输出.json] [--sensor 宽x高]");
    Console.WriteLine("  不带 --out 时把 JSON 草稿打到屏幕。");
    return 1;
}

var solPath = args[0];
char[] separators = { 'x', '*' };
if (!File.Exists(solPath))
{
    Console.Error.WriteLine($"找不到方案文件：{solPath}");
    return 1;
}

// ── 可选参数 ──
string? outPath = null;
int sensorW = 0, sensorH = 0;
    for (var i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--out") outPath = args[i + 1];
        if (args[i] == "--sensor")
        {
            var parts = args[i + 1].Split(separators);
        if (parts.Length == 2 && int.TryParse(parts[0], out sensorW) && int.TryParse(parts[1], out sensorH))
            continue;
        Console.Error.WriteLine($"--sensor 格式应为 宽x高，例如 2448x2048，收到：{args[i + 1]}");
        return 1;
    }
}

// ── 打开 .sol（ZIP 容器，2026-10-28 实证：PK 头 + SolutionFile/ 目录）──
ZipArchive zip;
try
{
    zip = ZipFile.OpenRead(solPath);
}
catch (InvalidDataException)
{
    Console.Error.WriteLine("这不是有效的 .sol 包（VM 方案是 ZIP 容器）。文件可能损坏或不是 VM 导出的方案。");
    return 1;
}

// 不用 GetEntry：它按原始名精确匹配，而不同 VM 版本导出的分隔符/大小写有差异（实测踩过）。
// 统一规范成小写正斜杠再比，容得住导出工具的脾气。
static ZipArchiveEntry? FindEntry(ZipArchive zip, string path) =>
    zip.Entries.FirstOrDefault(e =>
        e.FullName.Replace('\\', '/').Equals(path, StringComparison.OrdinalIgnoreCase));

var manifest = FindEntry(zip, "SolutionFile/VmServer.xml");
if (manifest is null)
{
    Console.Error.WriteLine("包里没有 SolutionFile/VmServer.xml——这可能不是 VisionMaster 4.x 导出的方案。");
    return 1;
}

var doc = XDocument.Load(manifest.Open());

// ── 提取：方案名 / 流程名 / 模块名册 ──
var solutionName = doc.Descendants("Solution").FirstOrDefault()?.Attribute("SolutionName")?.Value
                   ?? Path.GetFileNameWithoutExtension(solPath);
var procedures = doc.Descendants("Procedure")
    .Select(p => p.Attribute("DisplayName")?.Value ?? "")
    .Where(n => n.Length > 0).ToList();

// 名册 = 文档里出现的全部 Module 元素（含流程内的）。顺序 = 文档顺序（实证与模块 Index 一致）。
// 基础设施模块不是"检测内容"，不进 outputs：图像源、脚本、发送、通信管理、流程控制。
var infrastructure = new HashSet<string>
{
    "ImageSourceModule", "GlobalScript", "SendDatasModule", "CommManagerModule", "IMVSProcessControl",
};

var roster = doc.Descendants("Module")
    .Select(m => new
    {
        Name = m.Attribute("Name")?.Value ?? "",
        DisplayName = m.Attribute("DisplayName")?.Value ?? "",
        Index = (int?)m.Attribute("Index"),
    })
    .Where(m => m.Name.Length > 0)
    .GroupBy(m => m.DisplayName) // 同名只留一个（名册不该有重名，防御一下）
    .Select(g => g.First())
    .ToList();

var candidates = roster.Where(m => !infrastructure.Contains(m.Name)).ToList();
var saveImage = candidates.FirstOrDefault(m => m.Name == "SaveImage");
if (saveImage is not null)
    candidates.Remove(saveImage); // 存图模块是"可选的末段图像路径"，不是判定项——从 outputs 摘出，末尾提示手工追加

// 真实报文的第 1 段通常是逻辑汇总（AndModule 的运算结果）——预填时把它提到最前，
// 其余保持清单序。这只是草稿猜测，note 里会点名要求人工核对。
var logic = candidates.FirstOrDefault(m => m.Name.Contains("AndModule", StringComparison.OrdinalIgnoreCase) ||
                                          m.DisplayName.StartsWith("逻辑", StringComparison.Ordinal));
if (logic is not null)
{
    candidates.Remove(logic);
    candidates.Insert(0, logic);
}

if (candidates.Count == 0)
{
    Console.Error.WriteLine("名册里没有可作输出的模块（只有图像源/脚本等基础设施）——这份方案可能没有检测流程。");
    return 1;
}

// ── 组装 JSON 草稿 ──
// 顺序预填 = 名册文档序（BLOB分析1~5 天然排对）；逻辑汇总类通常在第 1 段，
// 但机器不知道订阅顺序，所以 note 里必须点名让人核对。
var orderText = string.Join("，", candidates.Select((m, i) => $"第{i + 1}段={m.DisplayName}"));
var note =
    $"报文段顺序（分隔符 ;）：{orderText}。注意：此顺序为草稿（按方案模块清单序预填），" +
    $"必须逐段对照 VisionMaster「发送数据」模块的订阅确认后再使用；改 VM 里的订阅顺序，必须同步改这里。" +
    (sensorW == 0 ? "sensor 为占位 0，请按相机实际分辨率填写。" : "") +
    $"（本文件由 RecipeGen 从 {Path.GetFileName(solPath)} 生成）";

var solutionPath = solPath.Replace('/', Path.DirectorySeparatorChar);
var marker = $"Config{Path.DirectorySeparatorChar}solutions{Path.DirectorySeparatorChar}";
var idx = solutionPath.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
var relSolutionPath = idx >= 0
    ? solutionPath[idx..].Replace(Path.DirectorySeparatorChar, '\\') // 配方里惯用反斜杠
    : Path.GetFullPath(solutionPath);

var procedure = procedures.Count > 0 ? string.Join("、", procedures) : "流程1";

var draft = new
{
    name = solutionName,
    schemaVersion = 2,
    sensor = new { width = sensorW, height = sensorH },
    engine = "Vm",
    note,
    vm = new
    {
        solutionPath = relSolutionPath,
        password = "",
        procedure,
        outputs = candidates.Select(m => new
        {
            module = m.DisplayName,   // VM 里的模块显示名——解析与名册的契约面
            label = m.DisplayName,    // 想叫"螺丝1"之类的人话名，在这里改
            role = "Defect",          // 默认缺陷检测；尺寸类人工改 Dimension
        }).ToArray(),
    },
};

var json = JsonSerializer.Serialize(draft, new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文不转 \uXXXX
});

// ── 输出 ──
if (outPath is null)
{
    Console.WriteLine(json);
}
else
{
    File.WriteAllText(outPath, json, new UTF8Encoding(false)); // 无 BOM，与现有配方一致
    Console.WriteLine($"已写出：{outPath}");
}

// ── 人工确认清单（生成器的诚实边界，缺了它就会有人把草稿当成品）──
Console.Error.WriteLine();
Console.Error.WriteLine("── 生成完成，但有 4 件事必须人工确认 ──");
Console.Error.WriteLine($"  1. 顺序：默认按模块清单序预填，逐段对照 VM「发送数据」订阅（{candidates.Count} 段）");
Console.Error.WriteLine("  2. 角色：全部默认 Defect，尺寸/计数类改成对应角色");
Console.Error.WriteLine("  3. 名称：label 现在等于 VM 显示名，可改成产线叫法（如 螺丝1）");
Console.Error.WriteLine(sensorW == 0
    ? "  4. 分辨率：sensor 是 0 占位，用 --sensor 宽x高 或手工填"
    : $"  4. 分辨率：已按 {sensorW}x{sensorH} 填写，请核对");
if (saveImage is not null)
    Console.Error.WriteLine("  附：名册里有输出图像模块（SaveImage）——若 VM 订阅了本地存图路径，可手工追加第 "
                           + (candidates.Count + 1) + " 段。");
Console.Error.WriteLine($"  名册共 {roster.Count} 个模块：{string.Join("、", roster.Select(m => m.DisplayName))}");
return 0;
