using Microsoft.Extensions.Logging.Abstractions;
using VisionStation.App.Services.Inspection;
using VisionStation.Devices;

namespace VisionStation.App.Tests;

/// <summary>
/// 工作流的"主动检测"契约。
///
/// 2026-10-26 相机层删除后，本程序不再采图：图像由平台产生、结果由平台推送。
/// 所以 <c>RunOnceAsync</c> 只剩一件必须守住的事 —— **什么都不做，且绝不入库**。
/// 自动循环仍会按节拍调进来，若每次造一条结果，良率统计就被污染了。
///
/// 原先的四个用例（采不到图、正常一帧、存图失败、取消）测的是已经不存在的采集路径，
/// 随相机层一起删除；替代它们的是下面的"空转且不落库"。
/// </summary>
public class InspectionWorkflowTests
{
    [Fact]
    public async Task 主动检测_永远返回null且不入库()
    {
        var repo = new FakeInspectionRepository();
        using var workflow = new InspectionWorkflow(
            new FakeEngine(), repo, NullLogger<InspectionWorkflow>.Instance);

        var result = await workflow.RunOnceAsync(TriggerOrigin.Manual, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, repo.SaveCount);
    }
}
