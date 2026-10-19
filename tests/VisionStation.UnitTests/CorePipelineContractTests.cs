using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace VisionStation.UnitTests;

/// <summary>
/// 阶段3 · core-main 切片：Pipeline 契约。
/// R-201 空检测必须判 NG；R-202 工具名必须唯一；R-203 ROI 匹配降级必须可观察。
/// </summary>
public class CorePipelineContractTests
{
    private sealed class FakeTool : IVisionTool
    {
        public string Name { get; set; } = "Fake";
        public ToolResult Result { get; set; } = ToolResult.Ok("ok");
        public int Calls { get; private set; }

        public ToolResult Execute(Mat grayImage, RoiRegion roi)
        {
            Calls++;
            return Result;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static Mat Image() => new(64, 64, MatType.CV_8UC1, new Scalar(120));

    private static List<RoiRegion> Rois(string name = "Presence")
        => new() { new RoiRegion { Name = name, X = 0, Y = 0, Width = 32, Height = 32 } };

    // ── R-201：没有任何工具结果 ≠ 合格 ──

    [Fact]
    public void Run_NoTools_ReportIsNg()
    {
        using var img = Image();

        var report = new Pipeline(Array.Empty<IVisionTool>())
            .Run(img, Rois(), new CalibrationConfig());

        Assert.Empty(report.Results);
        Assert.False(report.IsOk);
        Assert.Equal("NG", report.OverallVerdict);
    }

    [Fact]
    public void Run_NoRoiMatched_ReportIsNg()
    {
        using var img = Image();
        var tool = new FakeTool { Name = "Presence" };

        var report = new Pipeline(new[] { tool })
            .Run(img, new List<RoiRegion>(), new CalibrationConfig());

        Assert.Empty(report.Results);
        Assert.Equal(0, tool.Calls);          // 一个工具都没跑
        Assert.False(report.IsOk);            // 但绝不能报 OK
    }

    // ── R-202：工具名是报告主键，重名必须 fail-fast ──

    [Fact]
    public void Ctor_DuplicateToolNames_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => new Pipeline(new IVisionTool[]
        {
            new FakeTool { Name = "Presence" },
            new FakeTool { Name = "Presence" }
        }));

        Assert.Contains("Presence", ex.Message);
    }

    // ── R-203：名字匹配失败而落到索引兜底，必须留下警告 ──

    [Fact]
    public void Run_RoiNameMismatch_LogsWarningWithMatchMode()
    {
        using var img = Image();
        var log = new CapturingLogger<Pipeline>();
        var tool = new FakeTool { Name = "Presence" };

        // ROI 名字与工具名不一致 → 走"按顺序"兜底
        var report = new Pipeline(new[] { tool }, log)
            .Run(img, Rois("Other"), new CalibrationConfig());

        Assert.Single(report.Results);
        var warn = Assert.Single(log.Entries.Where(e => e.Level == LogLevel.Warning));
        Assert.Contains("Presence", warn.Message);
        Assert.Contains("Other", warn.Message);
        Assert.Contains("Index", warn.Message);
    }

    [Fact]
    public void Run_RoiNameMatched_NoFallbackWarning()
    {
        using var img = Image();
        var log = new CapturingLogger<Pipeline>();
        var tool = new FakeTool { Name = "Presence" };

        new Pipeline(new[] { tool }, log).Run(img, Rois("Presence"), new CalibrationConfig());

        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning);
    }
}
