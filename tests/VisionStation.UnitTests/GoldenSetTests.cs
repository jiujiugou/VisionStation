using VisionStation.GoldenSet;

namespace VisionStation.UnitTests;

/// <summary>
/// 黄金样本库评估逻辑的单元测试。
///
/// 为什么这组测试有价值？
///   跑批器本身只是"循环读图 → 调模型"，真正会写错的是**判定规则**和**对比规则**：
///   "有框但类别不匹配"算漏检还是错分、"样本顺序变了会不会比错"——
///   这些错了，跑出来的对比表就是假的，而且还看不出来。
/// </summary>
public class GoldenSetTests
{
    // ══ 判定规则 ══

    [Fact]
    public void Judge_NoBoxes_IsMissed()
    {
        var (verdict, other) = GoldenSetEvaluator.Judge("crazing", new Dictionary<string, int>());
        Assert.Equal(Verdict.Missed, verdict);
        Assert.Equal(0, other);
    }

    [Fact]
    public void Judge_OnlyOtherClasses_IsMisclassified()
    {
        var counts = new Dictionary<string, int> { ["patches"] = 2, ["inclusion"] = 1 };
        var (verdict, other) = GoldenSetEvaluator.Judge("crazing", counts);
        Assert.Equal(Verdict.Misclassified, verdict);
        Assert.Equal(3, other);   // 全部框都是跨类误报
    }

    [Fact]
    public void Judge_ExpectedPresent_IsHit_AndCountsExtraBoxesAsFalsePositive()
    {
        var counts = new Dictionary<string, int> { ["crazing"] = 2, ["patches"] = 1 };
        var (verdict, other) = GoldenSetEvaluator.Judge("crazing", counts);
        Assert.Equal(Verdict.Hit, verdict);
        Assert.Equal(1, other);   // 只有非真值类那 1 个框算误报
    }

    [Fact]
    public void Judge_ExpectedPresentAlone_IsHit_WithoutFalsePositive()
    {
        var counts = new Dictionary<string, int> { ["pitted_surface"] = 3 };
        var (verdict, other) = GoldenSetEvaluator.Judge("pitted_surface", counts);
        Assert.Equal(Verdict.Hit, verdict);
        Assert.Equal(0, other);
    }

    // ══ 对比规则 ══

    [Fact]
    public void Diff_HitBecomesMiss_IsRegression()
    {
        var baseline = Report(("a", "crazing", Verdict.Hit, new() { ["crazing"] = 2 }));
        var current = Report(("a", "crazing", Verdict.Missed, new()));

        var diff = GoldenSetEvaluator.Diff(baseline, current);

        Assert.Single(diff.Regressions);
        Assert.Equal("a", diff.Regressions[0].Id);
        Assert.Empty(diff.Fixes);
        Assert.True(diff.HasRegression);
    }

    [Fact]
    public void Diff_MissBecomesHit_IsFix()
    {
        var baseline = Report(("a", "crazing", Verdict.Missed, new()));
        var current = Report(("a", "crazing", Verdict.Hit, new() { ["crazing"] = 1 }));

        var diff = GoldenSetEvaluator.Diff(baseline, current);

        Assert.Single(diff.Fixes);
        Assert.Empty(diff.Regressions);
        Assert.False(diff.HasRegression);
    }

    [Fact]
    public void Diff_MisclassifiedBecomesMiss_IsRegression()
    {
        // 有框认错 → 一个框都没有，是恶化（信息量变少了）
        var baseline = Report(("a", "crazing", Verdict.Misclassified, new() { ["patches"] = 1 }));
        var current = Report(("a", "crazing", Verdict.Missed, new()));

        var diff = GoldenSetEvaluator.Diff(baseline, current);

        Assert.Single(diff.Regressions);
        Assert.True(diff.HasRegression);
    }

    [Fact]
    public void Diff_MatchesById_NotBySequencePosition()
    {
        // 基线顺序 a,b；本次顺序 b,a。按位置比会得出两条假回归，
        // 按 Id 比必须得出"完全一致"。
        var baseline = Report(
            ("a", "crazing", Verdict.Hit, new() { ["crazing"] = 1 }),
            ("b", "patches", Verdict.Missed, new()));
        var current = Report(
            ("b", "patches", Verdict.Missed, new()),
            ("a", "crazing", Verdict.Hit, new() { ["crazing"] = 1 }));

        var diff = GoldenSetEvaluator.Diff(baseline, current);

        Assert.Empty(diff.Regressions);
        Assert.Empty(diff.Fixes);
        Assert.Empty(diff.ChangedDetail);
        Assert.False(diff.HasRegression);
    }

    [Fact]
    public void Diff_MoreFalsePositiveBoxes_CountsAsRegression()
    {
        var baseline = Report(("a", "crazing", Verdict.Hit, new() { ["crazing"] = 1 }));
        var current = Report(("a", "crazing", Verdict.Hit, new() { ["crazing"] = 1, ["patches"] = 3 }));

        var diff = GoldenSetEvaluator.Diff(baseline, current);

        Assert.Empty(diff.Regressions);       // 判定没变
        Assert.Equal(3, diff.OtherBoxesDelta);
        Assert.True(diff.HasRegression);      // 但误报增加，仍然算回归
    }

    [Fact]
    public void Diff_SameVerdictDifferentComposition_GoesToChangedDetail()
    {
        var baseline = Report(("a", "crazing", Verdict.Hit, new() { ["crazing"] = 3 }));
        var current = Report(("a", "crazing", Verdict.Hit, new() { ["crazing"] = 1 }));

        var diff = GoldenSetEvaluator.Diff(baseline, current);

        Assert.Empty(diff.Regressions);
        Assert.Single(diff.ChangedDetail);
    }

    [Fact]
    public void Diff_IgnoresNewSamples_WhenJudgingRegression()
    {
        var baseline = Report(("a", "crazing", Verdict.Hit, new() { ["crazing"] = 1 }));
        var current = Report(
            ("a", "crazing", Verdict.Hit, new() { ["crazing"] = 1 }),
            ("new", "patches", Verdict.Missed, new()));

        var diff = GoldenSetEvaluator.Diff(baseline, current);

        // 新样本只影响总数统计，不该被报成"新漏检"
        Assert.Empty(diff.Regressions);
    }

    // ══ 汇总规则 ══

    [Fact]
    public void Summarize_CountsVerdictsAndPerClass()
    {
        var dataset = Dataset("crazing", "patches", "scratches");
        var report = Report(
            ("a", "crazing", Verdict.Hit, new() { ["crazing"] = 2, ["patches"] = 1 }),
            ("b", "crazing", Verdict.Missed, new()),
            ("c", "patches", Verdict.Misclassified, new() { ["crazing"] = 2 }));

        var m = GoldenSetEvaluator.Summarize(report, dataset);

        Assert.Equal(3, m.Total);
        Assert.Equal(1, m.Hit);
        Assert.Equal(1, m.Misclassified);
        Assert.Equal(1, m.Missed);
        Assert.Equal(3, m.OtherClassBoxes);   // a 的 1 个 + c 的 2 个

        var crazing = Assert.Single(m.PerClass.Where(c => c.Class == "crazing"));
        Assert.Equal(2, crazing.Total);
        Assert.Equal(0.5, crazing.RecallStrict, 3);

        var patches = Assert.Single(m.PerClass.Where(c => c.Class == "patches"));
        Assert.Equal(0.0, patches.RecallStrict, 3);
        Assert.Equal(1.0, patches.RecallLoose, 3);   // 错分也算检出了东西
    }

    [Fact]
    public void Summarize_ExposesUncoveredClasses()
    {
        var dataset = Dataset("crazing", "patches", "scratches");
        var report = Report(("a", "crazing", Verdict.Hit, new() { ["crazing"] = 1 }));

        var m = GoldenSetEvaluator.Summarize(report, dataset);

        // rolled-in_scale / scratches 在数据集里零样本，必须显式暴露
        Assert.Contains("patches", m.UncoveredClasses);
        Assert.Contains("scratches", m.UncoveredClasses);
        Assert.DoesNotContain("crazing", m.UncoveredClasses);
    }

    [Fact]
    public void Summarize_EmptyReport_DoesNotDivideByZero()
    {
        var m = GoldenSetEvaluator.Summarize(Report(), Dataset("crazing"));
        Assert.Equal(0, m.Total);
        Assert.Equal(0, m.RecallStrict);
        Assert.Equal(0, m.MissRate);
    }

    // ══ 构造辅助 ══

    private static GoldenDataset Dataset(params string[] classNames) => new()
    {
        Version = "test",
        ClassNames = classNames,
        Items = new List<GoldenItem>()
    };

    private static RunReport Report(params (string Id, string Expected, Verdict Verdict, Dictionary<string, int> Counts)[] items)
    {
        var report = new RunReport { Timestamp = "2026-01-01T00:00:00", DatasetVersion = "test" };
        foreach (var (id, expected, verdict, counts) in items)
        {
            report.Items.Add(new ItemOutcome
            {
                Id = id,
                Expected = expected,
                Verdict = verdict,
                ClassCounts = new SortedDictionary<string, int>(counts),
                OtherClassBoxes = counts.Where(kv => kv.Key != expected).Sum(kv => kv.Value),
                InferenceMs = 10
            });
        }
        return report;
    }
}
