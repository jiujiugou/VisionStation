using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;

namespace VisionStation.GoldenSet;

/// <summary>
/// 黄金样本库跑批器 —— 把"我觉得更准了"变成"漏检从 6 个变成 4 个"。
///
/// 行为：
///   run      跑全部样本 → 打印分类别指标 → 与 baseline 对比 → 写 last-run.json
///   verify   只校验数据集完整性（图片存在 + 哈希 + 尺寸），不加载模型
///
/// 退出码：
///   0 = 无回归
///   1 = 有回归（判定变差或跨类误报框增加），可直接接 CI 当门禁
///   2 = 运行失败（数据集校验不通过 / 模型缺失）
///
/// 关键约定：
///   所有样本都走真实的 Pipeline → OnnxYoloTool 链路，不绕过任何预处理，
///   否则测出来的数字和产线跑出来的不是一回事。
/// </summary>
internal static class Program
{
    private const string ToolName = "Yolo";
    private const int ExitOk = 0;
    private const int ExitRegression = 1;
    private const int ExitError = 2;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private static int Main(string[] args)
    {
        try
        {
            return Execute(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[失败] {ex.Message}");
            return ExitError;
        }
    }

    private static int Execute(string[] args)
    {
        var opts = Options.Parse(args);
        if (opts.ShowHelp)
        {
            PrintHelp();
            return ExitOk;
        }

        var root = FindRepoRoot();
        var datasetPath = Path.GetFullPath(Path.Combine(root, opts.DatasetPath));
        if (!File.Exists(datasetPath))
        {
            Console.Error.WriteLine($"[失败] 数据集清单不存在：{datasetPath}");
            return ExitError;
        }

        var dataset = JsonSerializer.Deserialize<GoldenDataset>(File.ReadAllText(datasetPath), JsonOpts)
                      ?? throw new InvalidOperationException("数据集清单反序列化失败");

        // ══ verify：只做完整性校验 ══
        var datasetErrors = VerifyDataset(root, dataset);
        if (opts.Command == "verify")
        {
            PrintDatasetInfo(dataset, root);
            if (datasetErrors.Count > 0)
            {
                Console.WriteLine($"\n[失败] 数据集校验未通过，{datasetErrors.Count} 个问题：");
                foreach (var e in datasetErrors) Console.WriteLine($"  - {e}");
                return ExitError;
            }
            Console.WriteLine("\n[通过] 数据集完整，全部样本哈希与尺寸匹配");
            return ExitOk;
        }

        if (datasetErrors.Count > 0)
        {
            Console.Error.WriteLine($"[失败] 数据集校验未通过（{datasetErrors.Count} 个问题）：");
            foreach (var e in datasetErrors.Take(10)) Console.Error.WriteLine($"  - {e}");
            Console.Error.WriteLine("先跑 `verify` 看完整清单，或用 --update-baseline 前修好数据。");
            return ExitError;
        }

        // ══ 模型 ══
        var modelPath = Path.GetFullPath(Path.Combine(root, opts.ModelPath));
        if (!File.Exists(modelPath))
        {
            Console.Error.WriteLine($"[失败] 模型文件不存在：{modelPath}");
            return ExitError;
        }
        var modelSha = Sha16(modelPath);

        PrintDatasetInfo(dataset, root);
        Console.WriteLine($"模型    {Rel(root, modelPath)}  sha {modelSha}  conf {opts.Conf:F2}  nms {opts.Nms:F2}");

        // ══ 跑批 ══
        var report = RunAll(root, dataset, modelPath, modelSha, opts);
        var metrics = GoldenSetEvaluator.Summarize(report, dataset);

        Console.WriteLine($"耗时    {report.TotalSeconds:F1}s" +
                          $"（均 {metrics.AvgInferenceMs:F0}ms/张，P95 {metrics.P95InferenceMs:F0}ms）");
        Console.WriteLine();

        PrintMetrics(metrics);

        // ══ 落盘 ══
        var outPath = Path.GetFullPath(Path.Combine(root, opts.OutputPath));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(report, JsonOpts));
        Console.WriteLine($"\n结果已写入 {Rel(root, outPath)}");

        // ══ 对比 ══
        int exit = ExitOk;
        var baselinePath = Path.GetFullPath(Path.Combine(root, opts.BaselinePath));

        if (opts.UpdateBaseline)
        {
            File.WriteAllText(baselinePath, JsonSerializer.Serialize(report, JsonOpts));
            Console.WriteLine($"基线已更新 {Rel(root, baselinePath)}");
        }
        else if (File.Exists(baselinePath))
        {
            var baseline = JsonSerializer.Deserialize<RunReport>(File.ReadAllText(baselinePath), JsonOpts);
            if (baseline != null)
            {
                var diff = GoldenSetEvaluator.Diff(baseline, report);
                PrintDiff(diff, root);
                if (diff.HasRegression) exit = ExitRegression;
            }
        }
        else
        {
            Console.WriteLine("（尚无基线，加 --update-baseline 建立第一份）");
        }

        return exit;
    }

    // ═══════════════════════════════════════════════════════
    //  跑批
    // ═══════════════════════════════════════════════════════

    private static RunReport RunAll(
        string root, GoldenDataset dataset, string modelPath, string modelSha, Options opts)
    {
        var report = new RunReport
        {
            Timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            DatasetVersion = dataset.Version,
            ModelSha = modelSha,
            ModelPath = Rel(root, modelPath),
            ConfThreshold = opts.Conf,
            NmsThreshold = opts.Nms
        };

        using var tool = new OnnxYoloTool(modelPath, opts.Conf, opts.Nms) { Name = ToolName };

        // Warmup：首帧要付模型加载 + 算子融合的代价（100~300ms），
        // 不预热的话第一张图的耗时会污染平均值和 P95，让人误判"算法变慢了"。
        var items = opts.Limit > 0 ? dataset.Items.Take(opts.Limit).ToList() : dataset.Items;
        if (items.Count == 0) return report;
        Warmup(tool, root, items[0]);

        var sw = Stopwatch.StartNew();
        int done = 0;

        foreach (var item in items)
        {
            var imgPath = Path.Combine(root, item.Image.Replace('/', Path.DirectorySeparatorChar));
            var outcome = new ItemOutcome { Id = item.Id, Expected = item.Expected };

            try
            {
                // 用 Unchanged 读：NEU-DET 是单通道灰度，保持原通道数才和工业黑白相机一致
                using var image = Cv2.ImRead(imgPath, ImreadModes.Unchanged);
                if (image.Empty())
                    throw new InvalidOperationException($"图片解码失败：{item.Image}");

                var rois = new List<RoiRegion>
                {
                    new() { Name = ToolName, X = 0, Y = 0, Width = image.Width, Height = image.Height }
                };

                // 走真实链路：Pipeline → IVisionTool，不直调工具
                var pipeline = new Pipeline(new IVisionTool[] { tool });
                var inspection = pipeline.Run(
                    image, rois, new CalibrationConfig(),
                    triggerSource: "GoldenSet", imagePath: item.Image);

                var result = inspection.GetToolResult(ToolName);

                var classCounts = result.Metrics
                    .Where(kv => kv.Key.StartsWith("Count_", StringComparison.Ordinal))
                    .ToDictionary(kv => kv.Key[6..], kv => (int)Math.Round(kv.Value));

                var (verdict, otherBoxes) = GoldenSetEvaluator.Judge(item.Expected, classCounts);
                outcome.Verdict = verdict;
                outcome.OtherClassBoxes = otherBoxes;
                outcome.MaxScore = result.Metrics.GetValueOrDefault("MaxScore");
                outcome.InferenceMs = result.Metrics.GetValueOrDefault("InferenceMs");
                outcome.ClassCounts = new SortedDictionary<string, int>(classCounts);
            }
            catch (Exception ex)
            {
                // 单张图失败不该中断整批 —— 记成漏检并带上原因，跑完一次性暴露
                outcome.Verdict = Verdict.Missed;
                outcome.Error = ex.Message;
            }

            report.Items.Add(outcome);

            if (++done % 20 == 0 || done == items.Count)
                Console.Write($"\r逐图检测 {done}/{items.Count} … ");
        }

        sw.Stop();
        report.TotalSeconds = sw.Elapsed.TotalSeconds;
        Console.Write("\r" + new string(' ', 40) + "\r");
        return report;
    }

    private static void Warmup(OnnxYoloTool tool, string root, GoldenItem first)
    {
        try
        {
            var path = Path.Combine(root, first.Image.Replace('/', Path.DirectorySeparatorChar));
            using var img = Cv2.ImRead(path, ImreadModes.Unchanged);
            if (img.Empty()) return;
            var roi = new RoiRegion { Name = ToolName, X = 0, Y = 0, Width = img.Width, Height = img.Height };
            using var gray = new Mat();
            if (img.Channels() > 1) Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
            else img.CopyTo(gray);
            tool.Execute(gray, roi);
        }
        catch
        {
            // 预热失败不阻断，真正的错误会在正式跑批时暴露
        }
    }

    // ═══════════════════════════════════════════════════════
    //  数据集校验
    // ═══════════════════════════════════════════════════════

    private static List<string> VerifyDataset(string root, GoldenDataset dataset)
    {
        var errors = new List<string>();

        if (dataset.Items.Count == 0)
            errors.Add("数据集为空");

        var seen = new HashSet<string>();
        foreach (var item in dataset.Items)
        {
            if (!seen.Add(item.Id))
                errors.Add($"{item.Id}: Id 重复");

            if (!dataset.ClassNames.Contains(item.Expected))
                errors.Add($"{item.Id}: 期望类别 \"{item.Expected}\" 不在 classNames 中");

            var path = Path.Combine(root, item.Image.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                errors.Add($"{item.Id}: 图片缺失 {item.Image}");
                continue;
            }

            var actual = Sha16(path);
            if (!string.Equals(actual, item.Sha256, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{item.Id}: 哈希不匹配（期望 {item.Sha256}，实际 {actual}）—— 图片被替换过");

            if (item.Width > 0 && item.Height > 0)
            {
                using var img = Cv2.ImRead(path, ImreadModes.Unchanged);
                if (!img.Empty() && (img.Width != item.Width || img.Height != item.Height))
                    errors.Add($"{item.Id}: 尺寸不匹配（期望 {item.Width}x{item.Height}，实际 {img.Width}x{img.Height}）");
            }
        }

        return errors;
    }

    // ═══════════════════════════════════════════════════════
    //  输出
    // ═══════════════════════════════════════════════════════

    private static void PrintDatasetInfo(GoldenDataset dataset, string root)
    {
        var perClass = dataset.Items.GroupBy(i => i.Expected)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key} {g.Count()}");
        Console.WriteLine($"数据集  v{dataset.Version} · {dataset.Items.Count} 张 · {string.Join(" / ", perClass)}");
    }

    private static void PrintMetrics(RunMetrics m)
    {
        Console.WriteLine("── 分类别 ──");
        Console.WriteLine(
            Pad("类别", 16) + PadLeft("样本", 6) + PadLeft("命中", 6) +
            PadLeft("错分", 6) + PadLeft("漏检", 6) + PadLeft("跨类误报", 10) + PadLeft("严格召回", 10));

        foreach (var c in m.PerClass)
        {
            Console.WriteLine(
                Pad(c.Class, 16) + PadLeft(c.Total.ToString(), 6) + PadLeft(c.Hit.ToString(), 6) +
                PadLeft(c.Misclassified.ToString(), 6) + PadLeft(c.Missed.ToString(), 6) +
                PadLeft(c.OtherClassBoxes.ToString(), 10) + PadLeft($"{c.RecallStrict:P1}", 10));
        }

        Console.WriteLine();
        Console.WriteLine("── 总体 ──");
        Console.WriteLine($"命中(真值类检出)  {m.Hit,4}/{m.Total}   {m.RecallStrict,6:P1}");
        Console.WriteLine($"错分(检出但认错)  {m.Misclassified,4}/{m.Total}   {m.MisclassRate,6:P1}");
        Console.WriteLine($"漏检(一个框都没)  {m.Missed,4}/{m.Total}   {m.MissRate,6:P1}");
        Console.WriteLine($"跨类误报框        {m.OtherClassBoxes,4} 个");

        if (m.UncoveredClasses.Count > 0)
            Console.WriteLine($"未覆盖类别        {string.Join(", ", m.UncoveredClasses)}（数据集无样本，指标不可用）");
    }

    private static void PrintDiff(RunDiff d, string root)
    {
        Console.WriteLine();
        Console.WriteLine($"── 与基线对比（{d.BaselineTimestamp} → {d.CurrentTimestamp}）──");

        if (d.Regressions.Count == 0 && d.Fixes.Count == 0 && d.ChangedDetail.Count == 0
            && d.OtherBoxesDelta == 0)
        {
            Console.WriteLine("判定完全一致，误报框数量无变化");
            return;
        }

        PrintTransitions("变差", d.Regressions, root);
        PrintTransitions("变好", d.Fixes, root);

        if (d.ChangedDetail.Count > 0)
        {
            Console.WriteLine($"细节变化  {d.ChangedDetail.Count} 个（判定未变，仅检出构成不同）");
            foreach (var t in d.ChangedDetail.Take(5))
                Console.WriteLine($"    {t.Id,-22} {t.Detail}");
            if (d.ChangedDetail.Count > 5)
                Console.WriteLine($"    … 其余 {d.ChangedDetail.Count - 5} 个见 last-run.json");
        }

        string sign = d.OtherBoxesDelta > 0 ? "+" : "";
        Console.WriteLine($"跨类误报框  {sign}{d.OtherBoxesDelta}（{d.OtherBoxesBefore} → {d.OtherBoxesAfter}）");
        Console.WriteLine($"平均耗时    {d.AvgMsDelta:+0;-0;0}ms（{d.AvgMsBefore:F0} → {d.AvgMsAfter:F0}）");
    }

    private static void PrintTransitions(string title, List<ItemTransition> list, string root)
    {
        if (list.Count == 0) return;
        Console.WriteLine($"{title}  {list.Count} 个");
        foreach (var t in list)
        {
            string tag = t.Delta > 0
                ? (t.After == Verdict.Missed ? "[新漏检]" : "[新错分]")
                : (t.After == Verdict.Hit ? "[修复漏检]" : "[改善]");
            Console.WriteLine($"    {tag} {t.Id,-22} {t.Detail}");
        }
    }

    // ═══════════════════════════════════════════════════════
    //  工具方法
    // ═══════════════════════════════════════════════════════

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VisionStation.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        // 发布 / 单文件运行时退回到当前工作目录
        return Directory.GetCurrentDirectory();
    }

    private static string Rel(string root, string abs)
        => Path.GetRelativePath(root, abs).Replace('\\', '/');

    private static string Sha16(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream))[..16].ToLowerInvariant();
    }

    private static int DisplayWidth(string s)
    {
        int w = 0;
        foreach (var ch in s) w += ch >= 0x2E80 ? 2 : 1;
        return w;
    }

    private static string Pad(string s, int width)
        => s + new string(' ', Math.Max(0, width - DisplayWidth(s)));

    private static string PadLeft(string s, int width)
        => new string(' ', Math.Max(0, width - DisplayWidth(s))) + s;

    private static void PrintHelp()
    {
        Console.WriteLine("""
            GoldenSetRunner — VisionStation 黄金样本库跑批器

            用法：
              GoldenSetRunner [run] [选项]     跑全部样本并与基线对比
              GoldenSetRunner verify           只校验数据集完整性
              GoldenSetRunner --help           显示本帮助

            选项：
              --update-baseline   把本次结果写成新基线（改完算法确认无误后再做）
              --conf <0..1>       置信度阈值，默认 0.25
              --nms  <0..1>       NMS IoU 阈值，默认 0.45
              --dataset <path>    数据集清单，默认 tests/GoldenSet/dataset.json
              --baseline <path>   基线文件，默认 tests/GoldenSet/baseline.json
              --out <path>        本次结果输出，默认 tests/GoldenSet/last-run.json
              --model <path>      模型文件，默认 models/neu-det.onnx
              --limit <n>         只跑前 n 张（调试用，不要拿来更新基线）

            退出码：0 无回归 / 1 有回归 / 2 运行失败
            """);
    }

    // ═══════════════════════════════════════════════════════
    //  参数
    // ═══════════════════════════════════════════════════════

    private sealed class Options
    {
        public string Command { get; init; } = "run";
        public bool ShowHelp { get; init; }
        public bool UpdateBaseline { get; init; }
        public float Conf { get; init; } = 0.25f;
        public float Nms { get; init; } = 0.45f;
        public string DatasetPath { get; init; } = "tests/GoldenSet/dataset.json";
        public string BaselinePath { get; init; } = "tests/GoldenSet/baseline.json";
        public string OutputPath { get; init; } = "tests/GoldenSet/last-run.json";
        public string ModelPath { get; init; } = "models/neu-det.onnx";
        public int Limit { get; init; }

        public static Options Parse(string[] args)
        {
            string command = "run";
            bool help = false, update = false, hasCmd = false;
            float conf = 0.25f, nms = 0.45f;
            string dataset = "tests/GoldenSet/dataset.json";
            string baseline = "tests/GoldenSet/baseline.json";
            string output = "tests/GoldenSet/last-run.json";
            string model = "models/neu-det.onnx";
            int limit = 0;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--help" or "-h": help = true; break;
                    case "--update-baseline": update = true; break;
                    case "--conf" when i + 1 < args.Length: conf = float.Parse(args[++i]); break;
                    case "--nms" when i + 1 < args.Length: nms = float.Parse(args[++i]); break;
                    case "--dataset" when i + 1 < args.Length: dataset = args[++i]; break;
                    case "--baseline" when i + 1 < args.Length: baseline = args[++i]; break;
                    case "--out" when i + 1 < args.Length: output = args[++i]; break;
                    case "--model" when i + 1 < args.Length: model = args[++i]; break;
                    case "--limit" when i + 1 < args.Length: limit = int.Parse(args[++i]); break;
                    default:
                        if (!args[i].StartsWith('-') && !hasCmd) { command = args[i]; hasCmd = true; }
                        break;
                }
            }

            return new Options
            {
                Command = command, ShowHelp = help, UpdateBaseline = update,
                Conf = conf, Nms = nms,
                DatasetPath = dataset, BaselinePath = baseline,
                OutputPath = output, ModelPath = model, Limit = limit
            };
        }
    }
}
