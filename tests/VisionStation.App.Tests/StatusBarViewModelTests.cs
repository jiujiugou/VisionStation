using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Tests;

/// <summary>
/// 状态栏的契约：累计 OK/NG 与节拍由状态栏自己记账。
///
/// 改造前这几个数字由检测编排方算好直接赋值 —— 编排方要记住"状态栏有哪几个字段"，
/// 而状态栏只要一改布局，编排方就跟着改。S4 把记账还给了显示方。
/// </summary>
public class StatusBarViewModelTests
{
    [Fact]
    public void 每帧累计OK与NG_并刷新节拍()
    {
        var vm = new StatusBarViewModel();

        WeakReferenceMessenger.Default.Send(
            new InspectionResultMessage(TestResults.Frame(ok: true)));
        WeakReferenceMessenger.Default.Send(
            new InspectionResultMessage(TestResults.Frame(ok: false)));

        Assert.Equal(1, vm.OkCount);
        Assert.Equal(1, vm.NgCount);
        Assert.Equal("12 ms", vm.CycleTime);
        Assert.Equal("83.3", vm.Fps);   // 1000 / 12
    }

    [Fact]
    public void 图像链路状态跟着消息走()
    {
        var vm = new StatusBarViewModel();

        WeakReferenceMessenger.Default.Send(new ImageLinkStatusMessage(false));
        Assert.False(vm.ImageLinkOnline);

        WeakReferenceMessenger.Default.Send(new ImageLinkStatusMessage(true));
        Assert.True(vm.ImageLinkOnline);
    }

    [Fact]
    public void 零耗时帧_不显示Infinity()
    {
        var vm = new StatusBarViewModel();
        var frame = TestResults.Frame() with { CycleMs = 0 };

        WeakReferenceMessenger.Default.Send(new InspectionResultMessage(frame));

        Assert.Equal("--", vm.Fps);
    }
}
