using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Tests;

/// <summary>
/// 结果面板的契约：**行由报告里的工具决定，不由界面写死**。
///
/// 改造前这里硬编码四行（特征有无 / 圆孔直径 / 划痕 / 缺口），
/// 划痕和缺口还是从同一条 Defect 结果里按 Metric 名抠出来的。
/// 配方一改，界面就少行或多行，而代码里看不出来 —— 这是 W-11。
/// </summary>
public class ResultPanelViewModelTests
{
    [Fact]
    public void 两个工具的配方_出两行_未知工具显示原名()
    {
        var vm = new ResultPanelViewModel();

        WeakReferenceMessenger.Default.Send(
            new InspectionResultMessage(TestResults.Frame(ok: true, "Presence", "OCR")));

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("特征有无", vm.Rows[0].Item);   // 认识的工具用中文名
        Assert.Equal("OCR", vm.Rows[1].Item);        // 不认识的工具不能被吞掉
        Assert.Equal("Presence 摘要", vm.Rows[0].Value);
        Assert.All(vm.Rows, row => Assert.True(row.IsOk));
        Assert.Equal("OK", vm.VerdictText);
        Assert.True(vm.IsOk);
    }

    [Fact]
    public void 五个工具的配方_出五行_顺序与报告一致()
    {
        var vm = new ResultPanelViewModel();

        WeakReferenceMessenger.Default.Send(
            new InspectionResultMessage(TestResults.Frame(ok: true,
                "Presence", "Dimension", "Defect", "OCR", "Barcode")));

        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal(new[] { "特征有无", "圆孔直径", "缺陷", "OCR", "Barcode" },
            vm.Rows.Select(r => r.Item));
    }

    [Fact]
    public void 任一工具NG_总判定NG_且该行状态文字为NG()
    {
        var vm = new ResultPanelViewModel();

        WeakReferenceMessenger.Default.Send(
            new InspectionResultMessage(TestResults.Frame(ok: false, "Presence")));

        Assert.Equal("NG", vm.VerdictText);
        Assert.False(vm.IsOk);
        Assert.Equal("NG", vm.Rows[0].StatusText);
        Assert.False(vm.Rows[0].IsOk);
    }

    [Fact]
    public void 新一帧覆盖旧行_不会累积()
    {
        var vm = new ResultPanelViewModel();

        WeakReferenceMessenger.Default.Send(
            new InspectionResultMessage(TestResults.Frame(ok: true, "Presence", "Dimension")));
        WeakReferenceMessenger.Default.Send(
            new InspectionResultMessage(TestResults.Frame(ok: true, "Presence")));

        Assert.Single(vm.Rows);
    }
}
