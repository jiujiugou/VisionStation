using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.Abstractions;
using VisionStation.App.ViewModels;
using VisionStation.Core.Engines;
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
    /// 急停取消必须什么都不做，包括不能留下审计。
    /// 如果取消也 Stop，操作员点“否”反而把产线停了，确认框就成了陷阱；
    /// 同理，取消却记一条"急停"会让审计页出现一次从未发生过的停机。
    /// </summary>
    [Fact]
    public async Task EmergencyStop_WhenCancelled_DoesNotStopTrigger()
    {
        var trigger = new FakeTrigger();
        var dialog = new FakeDialog { ConfirmResult = false };
        var audit = new FakeAuditService();
        var vm = new ControlPanelViewModel(trigger, dialog,
            new FakeEngine(), TestPaths.Recipe(), audit)
        {
            IsAutoMode = true
        };

        await vm.EmergencyStopCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.ConfirmCount);
        Assert.Equal(0, trigger.StopCount);
        Assert.True(vm.IsAutoMode);

        // 没确认就没有动作，也就不该有流水 —— 否则审计里会出现"没人按过的急停"
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// 急停确认后只 Stop 一次，并退出自动模式。
    /// 旧实现是同步 MessageBox，测试无法覆盖取消/确认两条路径。
    ///
    /// <para>
    /// 用例同时断言审计条目内容（F-13）：以前这里注入了 <see cref="FakeAuditService"/>
    /// 却从不看它记了什么，"埋点漏写"这种错就测不出来。
    /// </para>
    /// </summary>
    [Fact]
    public async Task EmergencyStop_WhenConfirmed_StopsTriggerOnce()
    {
        var trigger = new FakeTrigger();
        var dialog = new FakeDialog { ConfirmResult = true };
        var audit = new FakeAuditService();
        var vm = new ControlPanelViewModel(trigger, dialog,
            new FakeEngine(), TestPaths.Recipe(), audit)
        {
            IsAutoMode = true
        };

        await vm.EmergencyStopCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.ConfirmCount);
        Assert.Equal(1, trigger.StopCount);
        Assert.False(vm.IsAutoMode);

        // 急停必须留下且只留下一条流水，对象名是配方（事后要能回答"这班用的是哪份配方"）。
        // 注意断言的是 EStopPressed 而不是 EStopReset：本程序急停只是停触发源，
        // 没有任何"复位"命令，写 EStopReset 会把一个不存在动作说成发生过。
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditAction.EStopPressed, entry.Action);
        Assert.Equal("测试配方", entry.Target);
        Assert.Equal(AuditResult.Success, entry.Result);
        Assert.False(string.IsNullOrWhiteSpace(entry.Detail));
    }

    /// <summary>
    /// 清空要真的把消息发出去，且不能顺手删数据。
    ///
    /// 之前的实现只发一条"已清空"日志，没有任何面板订阅 —— 点了界面不变，
    /// 是典型的"有按钮没功能"。现在它发 <c>ClearAllMessage</c>，
    /// 各面板自己决定清什么；库里的记录与今日良率基数都不动。
    /// </summary>
    [Fact]
    public void ClearAll_BroadcastsMessage()
    {
        var received = 0;
        WeakReferenceMessenger.Default.Register<ClearAllMessage>(this, (_, _) => received++);

        try
        {
            var vm = new ControlPanelViewModel(new FakeTrigger(), new FakeDialog(),
                new FakeEngine(), TestPaths.Recipe(), new FakeAuditService());

            vm.ClearAllCommand.Execute(null);

            Assert.Equal(1, received);
        }
        finally
        {
            // 静态 Messenger 是进程级的：不反注册会让同进程的其他用例意外收到同一条消息
            WeakReferenceMessenger.Default.UnregisterAll(this);
        }
    }

    private sealed class FakeTrigger : ITriggerSource
    {
        public int StopCount { get; private set; }
        public bool IsRunning { get; private set; }
        public event Action<TriggerOrigin>? Triggered;

        public void Start() => IsRunning = true;

        public void Stop()
        {
            StopCount++;
            IsRunning = false;
        }

        public void Fire() => Triggered?.Invoke(TriggerOrigin.Manual);

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
