namespace VisionStation.GoldenSet;

/// <summary>
/// 黄金样本库的评估与对比逻辑 —— 纯函数，零 I/O、零 OpenCV 依赖。
///
/// 为什么单独抽出来？
///   这部分是"改算法前后能不能说清差异"的核心，必须能被单元测试覆盖。
///   如果埋在 Program.cs 里，就只能靠人工看输出判断对错——那等于没有验证。
/// </summary>
public static class GoldenSetEvaluator
{
    /// <summary>
    /// 汇总一次跑批的结果。
    /// </summary>
    /// <param name="report">跑批结果</param>
    /// <param name="dataset">数据集清单（提供类别全集，用于识别未覆盖类别）</param>
    public static RunMetrics Summarize(RunReport report, GoldenDataset dataset)
    {
        var metrics = new RunMetrics
        {
            Total = report.Items.Count,
            Hit = report.Items.Count(i => i.Verdict == Verdict.Hit),
            Misclassified = report.Items.Count(i => i.Verdict == Verdict.Misclassified),
            Missed = report.Items.Count(i => i.Verdict == Verdict.Missed),
            OtherClassBoxes = report.Items.Sum(i => i.OtherClassBoxes)
        };

        if (report.Items.Count > 0)
        {
            var times = report.Items.Select(i => i.InferenceMs).OrderBy(t => t).ToArray();
            metrics.AvgInferenceMs = times.Average();
            // P95 用 nearest-rank：小样本下比插值更诚实
            int idx = (int)Math.Ceiling(times.Length * 0.95) - 1;
            metrics.P95InferenceMs = times[Math.Clamp(idx, 0, times.Length - 1)];
        }

        // ── 按类别统计 ──
        foreach (var group in report.Items.GroupBy(i => i.Expected).OrderBy(g => g.Key))
        {
            metrics.PerClass.Add(new ClassMetric
            {
                Class = group.Key,
                Total = group.Count(),
                Hit = group.Count(i => i.Verdict == Verdict.Hit),
                Misclassified = group.Count(i => i.Verdict == Verdict.Misclassified),
                Missed = group.Count(i => i.Verdict == Verdict.Missed),
                OtherClassBoxes = group.Sum(i => i.OtherClassBoxes)
            });
        }

        // ── 未覆盖类别 ──
        // 这一项必须显式输出：数据集中没有的类别，指标里查不到，很容易被误读成"该类 100% 通过"
        var covered = metrics.PerClass.Select(c => c.Class).ToHashSet();
        metrics.UncoveredClasses = dataset.ClassNames
            .Where(c => !covered.Contains(c))
            .ToList();

        return metrics;
    }

    /// <summary>
    /// 对比两次运行 —— 这就是"改前漏检 N、改后漏检 M"的来源。
    ///
    /// 判定变差/变好的依据是 Verdict 的数值序（Hit=0 &lt; Misclassified=1 &lt; Missed=2），
    /// 所以 Delta &gt; 0 即回归，Delta &lt; 0 即修复。
    /// </summary>
    public static RunDiff Diff(RunReport baseline, RunReport current)
    {
        var diff = new RunDiff
        {
            BaselineTimestamp = baseline.Timestamp,
            CurrentTimestamp = current.Timestamp,
            OtherBoxesBefore = baseline.Items.Sum(i => i.OtherClassBoxes),
            OtherBoxesAfter = current.Items.Sum(i => i.OtherClassBoxes),
            AvgMsBefore = baseline.Items.Count == 0 ? 0 : baseline.Items.Average(i => i.InferenceMs),
            AvgMsAfter = current.Items.Count == 0 ? 0 : current.Items.Average(i => i.InferenceMs)
        };

        // 按 Id 匹配，不按顺序 —— 样本增删时顺序会错位，按位置比会得出完全错误的结论
        var before = baseline.Items.ToDictionary(i => i.Id);
        var after = current.Items.ToDictionary(i => i.Id);

        foreach (var (id, cur) in after)
        {
            if (!before.TryGetValue(id, out var prev))
                continue;   // 新增样本不参与回归判定

            var t = new ItemTransition(
                id, cur.Expected, prev.Verdict, cur.Verdict,
                $"检前:{prev.DetectedText} → 检后:{cur.DetectedText}");

            if (t.Delta > 0) diff.Regressions.Add(t);
            else if (t.Delta < 0) diff.Fixes.Add(t);
            else if (prev.DetectedText != cur.DetectedText) diff.ChangedDetail.Add(t);
        }

        // 回归按严重度降序（Missed 优先），同级按 Id 排序，保证输出稳定可复现
        diff.Regressions.Sort((a, b) =>
        {
            int c = b.Delta.CompareTo(a.Delta);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });
        diff.Fixes.Sort((a, b) =>
        {
            int c = a.Delta.CompareTo(b.Delta);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });
        diff.ChangedDetail.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        return diff;
    }

    /// <summary>
    /// 把工具的 Metrics 字典翻译成 Verdict。
    /// 独立成方法是为了能单测：这里的边界（"有框但类别不匹配"算错分还是漏检）最容易写错。
    /// </summary>
    /// <param name="expected">真值类别</param>
    /// <param name="classCounts">各类别检出框数（来自 ToolResult.Metrics 的 Count_xxx）</param>
    public static (Verdict Verdict, int OtherClassBoxes) Judge(
        string expected, IReadOnlyDictionary<string, int> classCounts)
    {
        int total = classCounts.Values.Sum();
        int expectedBoxes = classCounts.GetValueOrDefault(expected);

        // 一个框都没检出 → 漏检
        if (total == 0)
            return (Verdict.Missed, 0);

        // 有框但全是别的类 → 错分（说明模型看见了东西，但认错了）
        if (expectedBoxes == 0)
            return (Verdict.Misclassified, total);

        return (Verdict.Hit, total - expectedBoxes);
    }
}
