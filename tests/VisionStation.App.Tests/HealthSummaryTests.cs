using VisionStation.App.Composition;
using VisionStation.Service;

namespace VisionStation.App.Tests;

public class HealthSummaryTests
{
    [Fact]
    public void Describe_NoFailures_ReportsPass()
    {
        var results = new List<HealthResult>
        {
            new HealthResult("设备连接", true, "2/2 在线")
        };

        Assert.Equal("自检通过", HealthSummary.Describe(results));
    }

    [Fact]
    public void Describe_SingleFailure_NamesTheItem()
    {
        var results = new List<HealthResult>
        {
            new HealthResult("数据库", false, "db", "目录不可写")
        };

        Assert.Equal("自检 1 项异常: 数据库", HealthSummary.Describe(results));
    }

    [Fact]
    public void Describe_MultipleFailures_ListsOnlyFailedItemsInOrder()
    {
        var results = new List<HealthResult>
        {
            new HealthResult("数据库", false, "db", "目录不可写"),
            new HealthResult("存储路径", true, "ok"),
            new HealthResult("模板文件", false, "missing", "文件不存在")
        };

        Assert.Equal("自检 2 项异常: 数据库, 模板文件", HealthSummary.Describe(results));
    }
}
