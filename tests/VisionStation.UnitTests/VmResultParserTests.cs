using VisionStation.Core.Models;
using VisionStation.Engines.Vm;

namespace VisionStation.UnitTests;

/// <summary>
/// VM 结果解析。这一层是"判定权归平台"那条边界的落点，所以每条规则都要钉住：
///   上位机不改 VM 的结论，但**任何一个说不清的情况都必须变成 NG 并说明原因**。
/// </summary>
public class VmResultParserTests
{
    [Fact]
    public void 全项合格_报告为OK()
    {
        const string line = """
            {"verdict":"OK","items":[
              {"label":"外径","value":12.345,"unit":"mm","ok":true},
              {"label":"孔数","value":6,"ok":true}]}
            """;

        var report = VmResultParser.Parse(line);

        Assert.Equal("OK", report.OverallVerdict);
        Assert.Equal("VisionMaster", report.Engine);
        Assert.Equal(2, report.Results.Count);
        Assert.Equal("外径: 12.345mm", report.Results["外径"].Summary);
        Assert.Equal(12.345, report.Results["外径"].Metrics["外径"]);
    }

    [Fact]
    public void 任一项不合格_报告为NG()
    {
        const string line = """
            {"items":[
              {"label":"外径","value":12.4,"ok":true},
              {"label":"划伤","value":3,"ok":false}]}
            """;

        var report = VmResultParser.Parse(line);

        Assert.Equal("NG", report.OverallVerdict);
        Assert.True(report.Results["外径"].IsOk);
        Assert.False(report.Results["划伤"].IsOk);
        Assert.Contains("VM 判 NG", report.Results["划伤"].Summary);
    }

    [Fact]
    public void ok字段缺失_按NG处理()
    {
        // 没有判定的项不能算合格 —— 这条规则漏了，就会出现"VM 没给结论、上位机当 OK"的漏检
        const string line = """{"items":[{"label":"外径","value":12.3}]}""";

        var report = VmResultParser.Parse(line);

        Assert.Equal("NG", report.OverallVerdict);
        Assert.False(report.Results["外径"].IsOk);
        // 有值但判定缺失：摘要带数值，并且明确标出这是 NG
        Assert.Equal("外径: 12.3（VM 判 NG）", report.Results["外径"].Summary);
    }

    [Fact]
    public void 只有总判定_也能给出结论并注明无明细()
    {
        var ok = VmResultParser.Parse("""{"verdict":"OK"}""");
        Assert.Equal("OK", ok.OverallVerdict);
        Assert.Contains("未提供检测项明细", ok.Results[VmResultParser.DefaultVerdictLabel].Summary);

        var ng = VmResultParser.Parse("""{"verdict":"ng"}""");
        Assert.Equal("NG", ng.OverallVerdict);
    }

    [Fact]
    public void 既无检测项也无总判定_返回失败而不是静默合格()
    {
        var report = VmResultParser.Parse("""{"sn":"A001"}""");

        Assert.Equal("NG", report.OverallVerdict);
        Assert.True(report.Results.ContainsKey(ResultKeys.Engine));
        Assert.Contains("没有任何结论", report.Results[ResultKeys.Engine].Summary);
    }

    [Fact]
    public void 总判定NG但各项都OK_整体NG并说明原因()
    {
        // VM 里可能有没下发到 items 的否决条件（比如脚本里的综合判定）。
        // 判定权归 VM，所以以总判定为准，但必须留痕，否则事后查不出为什么 NG。
        const string line = """{"verdict":"NG","items":[{"label":"外径","value":12.3,"ok":true}]}""";

        var report = VmResultParser.Parse(line);

        Assert.Equal("NG", report.OverallVerdict);
        Assert.Contains("未下发的否决条件", report.Results[VmResultParser.DefaultVerdictLabel].Summary);
    }

    [Fact]
    public void 非法JSON_返回失败并带上原文片段()
    {
        var report = VmResultParser.Parse("{verdict:OK");

        Assert.Equal("NG", report.OverallVerdict);
        Assert.Contains("不是合法 JSON", report.Results[ResultKeys.Engine].Summary);
        Assert.Contains("{verdict:OK", report.Results[ResultKeys.Engine].Summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 空行_返回失败(string? line)
    {
        var report = VmResultParser.Parse(line);

        Assert.Equal("NG", report.OverallVerdict);
        Assert.Contains("空行", report.Results[ResultKeys.Engine].Summary);
    }

    [Fact]
    public void 触发来源与时间戳_按消息给的值()
    {
        const string line = """
            {"verdict":"OK","triggerSource":"PLC","timestamp":"2026-10-23T10:20:30+08:00"}
            """;

        var report = VmResultParser.Parse(line);

        Assert.Equal("PLC", report.TriggerSource);
        Assert.Equal(2026, report.Timestamp.Year);
        Assert.Equal(10, report.Timestamp.Hour);
    }

    [Fact]
    public void 触发来源缺失_记作VM()
    {
        Assert.Equal("VM", VmResultParser.Parse("""{"verdict":"OK"}""").TriggerSource);
        Assert.Equal("Fallback", VmResultParser.Parse("""{"verdict":"OK"}""", triggerSourceFallback: "Fallback").TriggerSource);
    }

    [Fact]
    public void Metric缺省时用Label_给了就用Metric()
    {
        const string line = """
            {"items":[
              {"label":"外径","value":12.3,"ok":true},
              {"label":"内径","value":6.1,"ok":true,"metric":"InnerDia"}]}
            """;

        var report = VmResultParser.Parse(line);

        Assert.True(report.Results["外径"].Metrics.ContainsKey("外径"));
        Assert.True(report.Results["内径"].Metrics.ContainsKey("InnerDia"));
    }

    [Fact]
    public void 结果带图片路径_原样保留()
    {
        const string line = """{"verdict":"OK","imagePath":"D:\\vm\\1.bmp"}""";

        Assert.Equal(@"D:\vm\1.bmp", VmResultParser.Parse(line).ImagePath);
    }

    [Fact]
    public void 无标签的项_用占位名而不是崩掉()
    {
        var report = VmResultParser.Parse("""{"items":[{"value":1,"ok":true}]}""");

        Assert.True(report.Results.ContainsKey("未命名项"));
    }

    // ── 分隔符格式（VM「发送数据」+ 分隔符 + 结束符 的原生产物）──────────────

    private static List<VmOutputMapping> 单输出() => new()
    {
        new VmOutputMapping { Module = "圆查找1", Label = "外径", Role = "Measure", Io = "Radius", Metric = "外径", Unit = "px" },
    };

    [Fact]
    public void 分隔符格式_OK加一个值_按配方顺序对上名字()
    {
        var report = VmResultParser.Parse("OK;12.345", outputs: 单输出());

        Assert.Equal("OK", report.OverallVerdict);
        Assert.Equal("外径: 12.345px", report.Results["外径"].Summary);
        Assert.Equal(12.345, report.Results["外径"].Metrics["外径"]);
    }

    [Fact]
    public void 分隔符格式_NG_各项跟随总判定()
    {
        var report = VmResultParser.Parse("NG;12.345", outputs: 单输出());

        Assert.Equal("NG", report.OverallVerdict);
        Assert.False(report.Results["外径"].IsOk);
    }

    [Fact]
    public void 分隔符格式_只有判定_算一条合法结论()
    {
        var report = VmResultParser.Parse("OK", outputs: 单输出());

        Assert.Equal("OK", report.OverallVerdict);
        Assert.True(report.Results.ContainsKey(VmResultParser.DefaultVerdictLabel));
    }

    [Fact]
    public void 分隔符格式_首段不是判定_判NG并说明原因()
    {
        var report = VmResultParser.Parse("12.345;8", outputs: 单输出());

        Assert.Equal("NG", report.OverallVerdict);
        Assert.Contains("不是判定", report.Results[ResultKeys.Engine].Summary);
    }

    [Fact]
    public void 分隔符格式_非数值项_保留文字不写Metric()
    {
        var report = VmResultParser.Parse("OK;有孔", outputs: 单输出());

        Assert.Equal("外径: 有孔", report.Results["外径"].Summary);
        Assert.False(report.Results["外径"].Metrics.ContainsKey("外径"));
    }

    [Fact]
    public void 分隔符格式_没给映射_用占位名而不是崩掉()
    {
        var report = VmResultParser.Parse("OK;12.345;8");

        Assert.True(report.Results.ContainsKey("值1"));
        Assert.True(report.Results.ContainsKey("值2"));
    }

    [Fact]
    public void 分隔符格式_模块状态2_按NG处理()
    {
        // VM 的「模块状态」是 0=未运行 / 1=OK / 2=NG，直接订阅它就能当总判定用
        Assert.Equal("NG", VmResultParser.Parse("2").OverallVerdict);
        Assert.Equal("OK", VmResultParser.Parse("1").OverallVerdict);
        Assert.Equal("NG", VmResultParser.Parse("0").OverallVerdict);
    }
}
