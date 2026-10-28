using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.Abstractions;
using VisionStation.App.Services.Interlock;
using VisionStation.App.ViewModels;
using VisionStation.Core.Engines;
using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Devices.Vm;
using VisionStation.Service;

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
            TestPaths.Recipe(), audit, new HealthSnapshot())
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
            TestPaths.Recipe(), audit, new HealthSnapshot())
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
                TestPaths.Recipe(), new FakeAuditService(), new HealthSnapshot());

            vm.ClearAllCommand.Execute(null);

            Assert.Equal(1, received);
        }
        finally
        {
            // 静态 Messenger 是进程级的：不反注册会让同进程的其他用例意外收到同一条消息
            WeakReferenceMessenger.Default.UnregisterAll(this);
        }
    }

    /// <summary>
    /// F-10（2026-10-27 拍板）：本程序不参与触发。
    ///
    /// "开始检测"只解释原因，**绝不推触发源** —— 推了也没有任何东西会检测，
    /// 但日志里会留下"已触发"的假象，比按钮没反应更难查。
    /// </summary>
    [Fact]
    public void StartDetection_DoesNotFireTrigger_ExplainsPlatformOwnsDetection()
    {
        var trigger = new FakeTrigger();
        var logs = new List<LogMessage>();

        WeakReferenceMessenger.Default.Register<LogMessage>(this, (_, msg) => logs.Add(msg));

        try
        {
            var vm = new ControlPanelViewModel(trigger, new FakeDialog(),
                TestPaths.Recipe(), new FakeAuditService(), new HealthSnapshot());

            vm.StartDetectionCommand.Execute(null);

            Assert.Equal(0, trigger.FireCount);

            var log = Assert.Single(logs);
            Assert.Equal(LogMsgLevel.Warning, log.Value.Level);
            Assert.Contains("VisionMaster", log.Value.Text);
        }
        finally
        {
            // 与 ClearAll 用例同理：静态 Messenger 不反注册会污染同进程其他用例
            WeakReferenceMessenger.Default.UnregisterAll(this);
        }
    }

    /// <summary>
    /// F-47：联锁不通过时，「进入自动运行」必须被真拦 —— 触发源一次都不许 Start、
    /// IsAutoMode 保持 false、不留 Start 流水，并且把每条失败原因单独写进日志
    /// （操作员要的是"哪几件事没就绪"，不是一句"未就绪"）。
    /// </summary>
    [Fact]
    public void ToggleAutoMode_联锁未通过_不Start不进自动_且逐条给原因()
    {
        var trigger = new FakeTrigger();
        var audit = new FakeAuditService();
        var logs = new List<LogMessage>();
        WeakReferenceMessenger.Default.Register<LogMessage>(this, (_, msg) => logs.Add(msg));

        try
        {
            // 什么都不配：链路状态未知 + 自检快照没跑 → 至少两项失败
            var vm = new ControlPanelViewModel(trigger, new FakeDialog(),
                TestPaths.Recipe(), audit, new HealthSnapshot());

            vm.ToggleAutoModeCommand.Execute(null);

            Assert.False(vm.IsAutoMode);
            Assert.Equal(0, trigger.StartCount);
            Assert.Empty(audit.Entries);   // 被拦的启动不是启动，不能留 Start 流水

            Assert.Contains(logs, m => m.Value.Level == LogMsgLevel.Warning && m.Value.Text.Contains("启动联锁未通过"));
            Assert.Contains(logs, m => m.Value.Text.Contains("VM 链路状态未知"));
            Assert.Contains(logs, m => m.Value.Text.Contains("启动自检尚未运行"));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);
        }
    }

    /// <summary>
    /// F-47：联锁全过时「进入自动运行」放行 —— Start 一次、进自动、留 Start 流水。
    /// </summary>
    [Fact]
    public void ToggleAutoMode_联锁通过_进入自动并Start一次()
    {
        var trigger = new FakeTrigger();
        var audit = new FakeAuditService();
        var vm = new ControlPanelViewModel(trigger, new FakeDialog(),
            TestPaths.Recipe(), audit, PassingHealth());

        try
        {
            WeakReferenceMessenger.Default.Send(new VmLinkStatusMessage(
                new VmLinkStatus(VmLinkState.Healthy, "127.0.0.1:9100", null, null,
                    TimeSpan.FromSeconds(10), true)));

            vm.ToggleAutoModeCommand.Execute(null);

            Assert.True(vm.IsAutoMode);
            Assert.Equal(1, trigger.StartCount);
            Assert.Contains(audit.Entries, e => e.Action == AuditAction.Start);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);
        }
    }

    /// <summary>
    /// F-47 主场景：急停按下 → 闩锁（复位按钮出现）→ 联锁拦自动运行 →
    /// 人工复位 → 放行。审计里 EStopPressed / EStopReset / Start 各一条，
    /// EStopReset 这个预留枚举值自此有了对应的真实动作。
    /// </summary>
    [Fact]
    public async Task 急停后_联锁拦自动_复位后才放行()
    {
        var trigger = new FakeTrigger();
        var dialog = new FakeDialog { ConfirmResult = true };
        var audit = new FakeAuditService();
        var vm = new ControlPanelViewModel(trigger, dialog,
            TestPaths.Recipe(), audit, PassingHealth());

        try
        {
            await vm.EmergencyStopCommand.ExecuteAsync(null);
            Assert.True(vm.HasPendingEStop);   // 复位按钮此刻才出现

            // 其余条件全齐，但急停未复位 → 拦
            WeakReferenceMessenger.Default.Send(new VmLinkStatusMessage(
                new VmLinkStatus(VmLinkState.Healthy, "127.0.0.1:9100", null, null,
                    TimeSpan.FromSeconds(10), true)));
            vm.ToggleAutoModeCommand.Execute(null);
            Assert.False(vm.IsAutoMode);
            Assert.Equal(0, trigger.StartCount);

            // 人工复位 → 放行
            vm.ResetEStopCommand.Execute(null);
            Assert.False(vm.HasPendingEStop);
            vm.ToggleAutoModeCommand.Execute(null);
            Assert.True(vm.IsAutoMode);
            Assert.Equal(1, trigger.StartCount);

            Assert.Contains(audit.Entries, e => e.Action == AuditAction.EStopPressed);
            Assert.Contains(audit.Entries, e => e.Action == AuditAction.EStopReset);
            Assert.Contains(audit.Entries, e => e.Action == AuditAction.Start);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);
        }
    }

    /// <summary>全过的自检快照：HasRun = true、无失败项。</summary>
    private static HealthSnapshot PassingHealth()
    {
        var snapshot = new HealthSnapshot();
        snapshot.Update(new[] { new HealthResult("存储", true, "可写") });
        return snapshot;
    }

    private sealed class FakeTrigger : ITriggerSource
    {
        public int StopCount { get; private set; }

        /// <summary>Fire 次数。F-10 的验收点：手动"开始检测"一次都不许推触发源。</summary>
        public int FireCount { get; private set; }

        /// <summary>Start 次数。F-47 的验收点：联锁不通过时一次都不许 Start。</summary>
        public int StartCount { get; private set; }

        public bool IsRunning { get; private set; }
        public event Action<TriggerOrigin>? Triggered;

        public void Start()
        {
            StartCount++;
            IsRunning = true;
        }

        public void Stop()
        {
            StopCount++;
            IsRunning = false;
        }

        public void Fire()
        {
            FireCount++;
            Triggered?.Invoke(TriggerOrigin.Manual);
        }

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
