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
    public void VM收帧丢帧消息_更新数字并标记有无丢帧()
    {
        // 丢帧是"有一件没被判过"，必须在常驻可见的地方露出来。
        // 状态栏只显示：数字口径在驱动层，这里不重算。
        var vm = new StatusBarViewModel();
        Assert.Equal("--", vm.VmFrameStats);   // VM 链路没启用时不能说"收 0 丢 0"

        WeakReferenceMessenger.Default.Send(new VmThroughputMessage(120, 0));
        Assert.Equal("收 120 / 丢 0", vm.VmFrameStats);
        Assert.False(vm.HasVmDrops);

        WeakReferenceMessenger.Default.Send(new VmThroughputMessage(121, 3));
        Assert.Equal("收 121 / 丢 3", vm.VmFrameStats);
        Assert.True(vm.HasVmDrops);           // 一旦丢过帧，这一格就变红，不再回到常态色
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
