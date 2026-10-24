using OpenCvSharp;
using VisionStation.Core.Models;
using VisionStation.Engines.Onnx;
using VisionStation.Engines.Vm;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// 引擎层守卫 —— 参数越界、失败语义、结果聚合。
///
/// 这些用例是改造前的 CoreInputGuardTests / CorePipelineContractTests 的继承者：
/// 被删掉的那部分测的是自研工具（标定比例、坐标换算、模板解码），
/// 随工具链一起移除；**保留下来的三条失效模式一条没少**：
///   · 阈值越界必须立刻失败（否则"没检出缺陷"被当成合格）
///   · 空结果集必须判 NG（没检 ≠ 合格）
///   · 引擎失败必须带原因，不抛异常给调用方
/// </summary>
public class EngineGuardTests
{
    // ── R-209：YOLO 参数越界必须立刻失败，不得静默"未检出" ──

    [Fact]
    public void YoloDetector_InvalidConfidence_Throws()
    {
        // 阈值 > 1：所有候选被过滤掉 → 返回"未检出缺陷" → 判 OK。
        // 模型什么都没判，系统却报合格 —— 必须在装配期就炸。
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloDetector("m.onnx", 1.5f, 0.45f, Classes, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloDetector("m.onnx", 0f, 0.45f, Classes, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloDetector("m.onnx", float.NaN, 0.45f, Classes, 100));
    }

    [Fact]
    public void YoloDetector_InvalidNms_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloDetector("m.onnx", 0.25f, 0f, Classes, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloDetector("m.onnx", 0.25f, 1.5f, Classes, 100));
    }

    [Fact]
    public void YoloDetector_InvalidLimits_Throw()
    {
        // MaxDetections=0 会把所有框裁掉，等价于"永远没有缺陷"
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloDetector("m.onnx", 0.25f, 0.45f, Classes, 0));
        Assert.Throws<ArgumentException>(() => new YoloDetector("m.onnx", 0.25f, 0.45f, Array.Empty<string>(), 100));
    }

    [Fact]
    public void YoloDetector_EmptyImage_Throws()
    {
        using var detector = new YoloDetector("m.onnx", 0.25f, 0.45f, Classes, 100);
        using var empty = new Mat();

        Assert.Throws<ArgumentException>(() => detector.Detect(empty, out _));
    }

    // ── F-C01：空结果集必须判 NG ──

    [Fact]
    public void Report_NoResults_IsNg()
    {
        var report = new InspectionReport();

        Assert.False(report.IsOk, "一项都没跑却判合格：Enumerable.All 对空集合返回 true（F-C01）");
        Assert.Equal("NG", report.OverallVerdict);
    }

    // ── 引擎失败语义：NG + 原因，且占 Engine 键 ──

    [Fact]
    public void Report_Failure_IsNgWithReason()
    {
        var report = InspectionReport.Failure("VisionMaster", "流程文件不存在：x.sol");

        Assert.False(report.IsOk);
        Assert.Equal("VisionMaster", report.Engine);
        Assert.True(report.Results.ContainsKey(ResultKeys.Engine));
        Assert.Contains("x.sol", report.GetToolResult(ResultKeys.Engine).Summary);
    }

    // ── 角色聚合：历史记录的三列靠它投影 ──

    [Fact]
    public void AggregateByRole_AnyNg_MakesRoleNg()
    {
        var report = new InspectionReport();
        report.SetToolResult("外径", new ToolResult { IsOk = true, Role = ResultKeys.Measure, Summary = "外径 OK" });
        report.SetToolResult("内径", new ToolResult { IsOk = false, Role = ResultKeys.Measure, Summary = "内径 NG" });

        var measure = report.AggregateByRole(ResultKeys.Measure);

        Assert.False(measure.IsOk, "同一角色下有一项 NG，聚合结果必须是 NG");
        Assert.Contains("内径", measure.Summary);
        Assert.Contains("外径", measure.Summary);
    }

    [Fact]
    public void AggregateByRole_MissingRole_IsNgNotOk()
    {
        var report = new InspectionReport();
        report.SetToolResult("外径", new ToolResult { IsOk = true, Role = ResultKeys.Measure, Summary = "OK" });

        var locate = report.AggregateByRole(ResultKeys.Locate);

        Assert.False(locate.IsOk, "没有该角色的结果必须是「未执行」NG，不能是「检了没问题」");
        Assert.Contains("未执行", locate.Summary);
    }

    private static readonly string[] Classes = { "scratch" };
}
