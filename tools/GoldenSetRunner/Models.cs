namespace VisionStation.GoldenSet;

// ═══════════════════════════════════════════════════════
//  黄金样本库的数据契约
//
//  设计原则：这里的类型只负责"装数据"，不含任何 OpenCV / ONNX 依赖，
//  这样评估逻辑（GoldenSetEvaluator）可以被单元测试直接调用，不需要真跑模型。
// ═══════════════════════════════════════════════════════

/// <summary>数据集中的一条样本</summary>
public sealed class GoldenItem
{
    /// <summary>样本 ID（图片文件名去扩展名，如 crazing_271）</summary>
    public string Id { get; set; } = "";

    /// <summary>相对仓库根的图片路径（正斜杠）</summary>
    public string Image { get; set; } = "";

    /// <summary>
    /// 真值类别。NEU-DET 无框级标注，标签来自文件名前缀，属于图像级真值。
    /// 语义：这张图里存在该类缺陷。
    /// </summary>
    public string Expected { get; set; } = "";

    /// <summary>图片内容 SHA256 前 16 位 —— 用于锁定数据版本，防止样本被悄悄替换</summary>
    public string Sha256 { get; set; } = "";

    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>数据集清单（tests/GoldenSet/dataset.json）</summary>
public sealed class GoldenDataset
{
    public string Version { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>模型类别表全集（含数据集中未覆盖的类别）</summary>
    public string[] ClassNames { get; set; } = Array.Empty<string>();

    public List<GoldenItem> Items { get; set; } = new();
}

/// <summary>
/// 单张图的判定结果。
///
/// 为什么只有三种状态？
///   黄金样本库当前全部是"有缺陷"的图（NEU-DET 无 good 样本），
///   所以不存在 TrueNegative。等 good 样本补齐后再扩展 Verdict。
/// </summary>
public enum Verdict
{
    /// <summary>检出了真值类缺陷 —— 合格判定</summary>
    Hit = 0,

    /// <summary>检出了框，但没有任何一个是真值类 —— 错分</summary>
    Misclassified = 1,

    /// <summary>一个框都没检出 —— 漏检，最严重</summary>
    Missed = 2
}

/// <summary>单张图的完整结果（跑批产物，也是 baseline 的内容）</summary>
public sealed class ItemOutcome
{
    public string Id { get; set; } = "";
    public string Expected { get; set; } = "";
    public Verdict Verdict { get; set; }

    /// <summary>各类别检出框数（只记非零项）</summary>
    public SortedDictionary<string, int> ClassCounts { get; set; } = new();

    /// <summary>非真值类的框总数 —— 即跨类误报框</summary>
    public int OtherClassBoxes { get; set; }

    public double MaxScore { get; set; }
    public double InferenceMs { get; set; }

    /// <summary>执行异常信息（工具抛异常时记录，判定为 Missed）</summary>
    public string? Error { get; set; }

    /// <summary>所有检出类别的可读串，如 "patches:2"（派生属性，不参与序列化）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DetectedText => ClassCounts.Count == 0
        ? "-"
        : string.Join(" ", ClassCounts.Select(kv => $"{kv.Key}:{kv.Value}"));
}

/// <summary>一次完整跑批的结果</summary>
public sealed class RunReport
{
    public string Timestamp { get; set; } = "";
    public string DatasetVersion { get; set; } = "";

    /// <summary>模型文件 SHA256 前 16 位 —— 对比时要能看出"是不是换了权重"</summary>
    public string ModelSha { get; set; } = "";
    public string ModelPath { get; set; } = "";

    public float ConfThreshold { get; set; }
    public float NmsThreshold { get; set; }
    public double TotalSeconds { get; set; }

    public List<ItemOutcome> Items { get; set; } = new();
}

/// <summary>单个类别的统计</summary>
public sealed class ClassMetric
{
    public string Class { get; set; } = "";
    public int Total { get; set; }
    public int Hit { get; set; }
    public int Misclassified { get; set; }
    public int Missed { get; set; }
    public int OtherClassBoxes { get; set; }

    /// <summary>严格召回：真值类必须被命中</summary>
    public double RecallStrict => Total == 0 ? 0 : (double)Hit / Total;

    /// <summary>宽松召回：真值类命中或至少检出了别的缺陷</summary>
    public double RecallLoose => Total == 0 ? 0 : (double)(Hit + Misclassified) / Total;

    /// <summary>漏检率：一个框都没检出</summary>
    public double MissRate => Total == 0 ? 0 : (double)Missed / Total;
}

/// <summary>全局统计 —— 这就是"漏检 N 个、误报 M 个"的来源</summary>
public sealed class RunMetrics
{
    public int Total { get; set; }
    public int Hit { get; set; }
    public int Misclassified { get; set; }
    public int Missed { get; set; }

    /// <summary>跨类误报框总数（按框计，不是按图计）</summary>
    public int OtherClassBoxes { get; set; }

    public double AvgInferenceMs { get; set; }
    public double P95InferenceMs { get; set; }

    public double RecallStrict => Total == 0 ? 0 : (double)Hit / Total;
    public double RecallLoose => Total == 0 ? 0 : (double)(Hit + Misclassified) / Total;
    public double MissRate => Total == 0 ? 0 : (double)Missed / Total;
    public double MisclassRate => Total == 0 ? 0 : (double)Misclassified / Total;

    /// <summary>按类别统计（只含有样本的类别）</summary>
    public List<ClassMetric> PerClass { get; set; } = new();

    /// <summary>数据集里零样本的类别 —— 必须显式暴露，否则会误以为"全覆盖"</summary>
    public List<string> UncoveredClasses { get; set; } = new();
}

/// <summary>单张图在两次运行之间的变化</summary>
public sealed record ItemTransition(
    string Id,
    string Expected,
    Verdict Before,
    Verdict After,
    string Detail)
{
    /// <summary>严重度变化：正数=变差，负数=变好，0=不变</summary>
    public int Delta => (int)After - (int)Before;
}

/// <summary>两次运行的对比结果</summary>
public sealed class RunDiff
{
    public string BaselineTimestamp { get; set; } = "";
    public string CurrentTimestamp { get; set; } = "";

    /// <summary>变差的样本（按严重度降序）</summary>
    public List<ItemTransition> Regressions { get; set; } = new();

    /// <summary>变好的样本</summary>
    public List<ItemTransition> Fixes { get; set; } = new();

    /// <summary>判定未变、但检出构成变了的样本（信息量较低，单列）</summary>
    public List<ItemTransition> ChangedDetail { get; set; } = new();

    public int OtherBoxesBefore { get; set; }
    public int OtherBoxesAfter { get; set; }
    public double AvgMsBefore { get; set; }
    public double AvgMsAfter { get; set; }

    public int OtherBoxesDelta => OtherBoxesAfter - OtherBoxesBefore;
    public double AvgMsDelta => AvgMsAfter - AvgMsBefore;

    /// <summary>本次对比是否发现问题（有回归或误报增加）</summary>
    public bool HasRegression => Regressions.Count > 0 || OtherBoxesDelta > 0;
}
