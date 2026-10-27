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
    /// 急停取消必须什么都不做。
    /// 如果取消也 Stop，操作员点“否”反而把产线停了，确认框就成了陷阱。
    /// </summary>
    [Fact]
    public async Task EmergencyStop_WhenCancelled_DoesNotStopTrigger()
    {
        var trigger = new FakeTrigger();
        var dialog = new FakeDialog { ConfirmResult = false };
        var vm = new ControlPanelViewModel(trigger, dialog,
            new FakeEngine(), TestPaths.Create(), TestPaths.Recipe(), new FakeAuditService())
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
        var vm = new ControlPanelViewModel(trigger, dialog,
            new FakeEngine(), TestPaths.Create(), TestPaths.Recipe(), new FakeAuditService())
        {
            IsAutoMode = true
        };

        await vm.EmergencyStopCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.ConfirmCount);
        Assert.Equal(1, trigger.StopCount);
        Assert.False(vm.IsAutoMode);
    }

    /// <summary>
    /// 重载必须先停触发、再退出自动模式。
    /// 重载期间进来的帧在引擎里会拿到 NG，但"正在自动运行"这个状态如果不变，
    /// 操作员会以为产线还在跑。
    /// </summary>
    [Fact]
    public void ReloadEngine_StopsTriggerAndClearsAutoMode()
    {
        var trigger = new FakeTrigger();
        var engine = new FakeEngine();
        var vm = new ControlPanelViewModel(trigger, new FakeDialog(),
            engine, TestPaths.Create(), TestPaths.Recipe(), new FakeAuditService())
        {
            IsAutoMode = true
        };

        vm.ReloadEngineCommand.Execute(null);

        Assert.Equal(1, trigger.StopCount);
        Assert.False(vm.IsAutoMode);
        Assert.Equal(1, engine.ReloadCount);
    }

    /// <summary>重载失败不能静默：必须把原因带出来（走查时靠日志定位"模块名改了"）。</summary>
    [Fact]
    public void ReloadEngine_WhenFailed_StillCallsEngineAndDoesNotThrow()
    {
        var engine = new FakeEngine { ReloadProblem = "方案里没有名为「流程1」的流程" };
        var vm = new ControlPanelViewModel(new FakeTrigger(), new FakeDialog(),
            engine, TestPaths.Create(), TestPaths.Recipe(), new FakeAuditService());

        vm.ReloadEngineCommand.Execute(null);   // 不抛异常即为通过

        Assert.Equal(1, engine.ReloadCount);
    }

    /// <summary>
    /// 引擎不支持扫描时（ONNX）点扫描不能崩，也不能写文件。
    /// 这是"可选能力用接口隔离"的目的：主接口不该被迫实现它。
    /// </summary>
    [Fact]
    public void ScanOutputs_WhenEngineCannotScan_DoesNotThrow()
    {
        var vm = new ControlPanelViewModel(new FakeTrigger(), new FakeDialog(),
            new FakeEngine(), TestPaths.Create(), TestPaths.Recipe(), new FakeAuditService());

        vm.ScanOutputsCommand.Execute(null);
    }

    /// <summary>
    /// ioKind 必须按运行时类型推断：圆类型的输出用 Float 去取会抛异常。
    /// </summary>
    [Fact]
    public void BuildScanJson_InfersIoKindFromRuntimeType()
    {
        var scan = new OutputScanResult(new[]
        {
            new ScannedModule("圆查找1", new[]
            {
                new ScannedOutput("Radius", "VmIOCircle"),
                new ScannedOutput("Score", "VmIOFloat")
            }),
            new ScannedModule("不存在的模块", Array.Empty<ScannedOutput>(), "流程「流程1」里没有名为「不存在的模块」的模块")
        }, null);

        var json = ControlPanelViewModel.BuildScanJson(scan, "VisionMaster");

        Assert.Contains("\"module\": \"圆查找1\"", json);
        Assert.Contains("\"io\": \"Radius\"", json);
        Assert.Contains("\"ioKind\": \"Circle\"", json);
        Assert.Contains("\"ioKind\": \"Float\"", json);
        Assert.Contains("\"engine\": \"VisionMaster\"", json);
        // 找不到的模块也要留在文件里，否则工程师不知道是漏配还是配错
        Assert.Contains("不存在的模块", json);
    }

    /// <summary>
    /// 扫描必须用配方里声明的模块名 —— 这是"核对配方对不对"的前提。
    /// </summary>
    [Fact]
    public void ScanOutputs_PassesRecipeModuleNamesToEngine()
    {
        var engine = new FakeScanningEngine
        {
            Result = new OutputScanResult(new[]
            {
                new ScannedModule("圆查找1", new[] { new ScannedOutput("Radius", "VmIOCircle") })
            }, null)
        };
        var vm = new ControlPanelViewModel(new FakeTrigger(), new FakeDialog(),
            engine, TestPaths.Create(), TestPaths.Recipe(), new FakeAuditService());

        vm.ScanOutputsCommand.Execute(null);

        Assert.Equal(new[] { "圆查找1" }, engine.LastScanNames);
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
