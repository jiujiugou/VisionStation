using VisionStation.App.Abstractions;
using VisionStation.App.ViewModels;
using VisionStation.Core.Models;
using VisionStation.Devices;

namespace VisionStation.App.Tests;

/// <summary>
/// 控制面板的命令契约。
///
/// 这些测试不创建 Application、不弹 MessageBox；对话框通过 IDialogService 替身注入。
/// </summary>
public class ControlPanelViewModelTests
{
    /// <summary>
    /// 急停取消必须什么都不做。
    /// 如果取消也 Stop，操作员点“否”反而把产线停了，确认框就成了陷阱。
    /// </summary>
    [Fact]
    public async Task EmergencyStop_WhenCancelled_DoesNotStopTrigger()
    {
        var trigger = new FakeTrigger();
        var dialog = new FakeDialog { ConfirmResult = false };
        var vm = new ControlPanelViewModel(trigger, new FakeCamera(), dialog)
        {
            IsAutoMode = true
        };

        await vm.EmergencyStopCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.ConfirmCount);
        Assert.Equal(0, trigger.StopCount);
        Assert.True(vm.IsAutoMode);
    }

    /// <summary>
    /// 急停确认后只 Stop 一次，并退出自动模式。
    /// 旧实现是同步 MessageBox，测试无法覆盖取消/确认两条路径。
    /// </summary>
    [Fact]
    public async Task EmergencyStop_WhenConfirmed_StopsTriggerOnce()
    {
        var trigger = new FakeTrigger();
        var dialog = new FakeDialog { ConfirmResult = true };
        var vm = new ControlPanelViewModel(trigger, new FakeCamera(), dialog)
        {
            IsAutoMode = true
        };

        await vm.EmergencyStopCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.ConfirmCount);
        Assert.Equal(1, trigger.StopCount);
        Assert.False(vm.IsAutoMode);
    }

    /// <summary>
    /// 重连必须调用 ICamera.open()，不能判断具体相机类型。
    /// DI 注入的是 SwappableCamera，`is FileCamera` 永远为 false；
    /// open() 会转发给当前实现，FileCamera 才真正重扫目录。
    /// </summary>
    [Fact]
    public void Reconnect_CallsCameraOpenAndUpdatesOnlineState()
    {
        var camera = new FakeCamera { IsAvailable = false, NextOpenSucceeds = true };
        var vm = new ControlPanelViewModel(new FakeTrigger(), camera, new FakeDialog());

        vm.ReconnectCommand.Execute(null);

        Assert.Equal(1, camera.OpenCount);
        Assert.True(vm.CameraOnline);
    }

    private sealed class FakeTrigger : ITriggerSource
    {
        public int StopCount { get; private set; }
        public bool IsRunning { get; private set; }
        public event Action? Triggered;

        public void Start() => IsRunning = true;

        public void Stop()
        {
            StopCount++;
            IsRunning = false;
        }

        public void Fire() => Triggered?.Invoke();

        public void Dispose() { }
    }

    private sealed class FakeCamera : ICamera
    {
        public bool IsAvailable { get; set; }
        public bool NextOpenSucceeds { get; set; }
        public int OpenCount { get; private set; }
        public int FrameCount => 0;

        public void open()
        {
            OpenCount++;
            IsAvailable = NextOpenSucceeds;
        }

        public void close() => IsAvailable = false;

        public CameraFrame? Capture() => null;

        public void Dispose() { }
    }

    private sealed class FakeDialog : IDialogService
    {
        public bool ConfirmResult { get; set; }
        public int ConfirmCount { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmCount++;
            return Task.FromResult(ConfirmResult);
        }

        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    }
}
