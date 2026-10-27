using OpenCvSharp;
using VisionStation.Core.Models;
using VisionStation.Devices;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// 设备层（U08-U15 / E05-E11）的三步法验证用例。
///
/// 相机层已在 2026-10-26 删除（相机归视觉平台独占），因此这里只剩触发源与设备管理器：
///   - 连接/断开必须真的转发到被包装对象
///   - 单台设备失败不能带走整轮 ConnectAll，且失败原因要带出设备自己记录的那句话
///   - 后台线程异常不能带走进程
/// </summary>
public class DeviceLayerTests
{
    // ═══════════════════════════════════════════════════
    //  替身
    // ═══════════════════════════════════════════════════

    private sealed class FakeTrigger : ITriggerSource
    {
        public bool IsRunning { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public event Action<TriggerOrigin>? Triggered;
        public void Start() { IsRunning = true; StartCount++; }
        public void Stop() { IsRunning = false; StopCount++; }
        public void Fire() => Triggered?.Invoke(TriggerOrigin.Manual);

        // 接口继承 IDisposable 后必须实现；替身无资源，空实现即可
        public void Dispose() => GC.SuppressFinalize(this);
    }

    private sealed class FakeDevice : IDevice
    {
        public string? LastError { get; set; }
        public FakeDevice(bool connected) => IsConnected = connected;
        public string Name => "fake";
        public string Type => "Fake";
        public bool IsConnected { get; set; }
        public string Status => IsConnected ? "在线" : "离线";
        public void Connect() { }
        public void Disconnect() { }
    }

    /// <summary>Connect 直接抛的设备：证明单台失败不会带走整个 ConnectAll</summary>
    private sealed class ThrowingDevice : IDevice
    {
        public string? LastError => null;
        public string Name => "boom";
        public string Type => "Fake";
        public bool IsConnected => false;
        public string Status => "离线";
        public void Connect() => throw new InvalidOperationException("设备被占用");
        public void Disconnect() { }
    }

    [Fact]
    public void TriggerDevice_DisconnectThenConnect_RestoresAutoMode()
    {
        var trigger = new FakeTrigger();
        var dev = new TriggerDevice(trigger);
        trigger.Start();                       // 自动模式在跑

        dev.Disconnect();
        Assert.False(trigger.IsRunning);       // 断开当然要停
        dev.Connect();
        Assert.True(trigger.IsRunning);        // 重连必须恢复，否则自动触发永久停摆
    }

    [Fact]
    public void TriggerDevice_ConnectWhenNeverRunning_DoesNotStartTimer()
    {
        var trigger = new FakeTrigger();
        var dev = new TriggerDevice(trigger);
        dev.Connect();
        Assert.Equal(0, trigger.StartCount);   // 手动模式不能被包装层偷偷启动
    }

    // ═══════════════════════════════════════════════════
    //  F-D11 空设备表不得报"全部在线"
    // ═══════════════════════════════════════════════════

    [Fact]
    public void DeviceManager_Empty_AllConnectedIsFalse()
    {
        var dm = new DeviceManager();
        Assert.False(dm.AllConnected);         // 0/0 报全在线 = 恒真判据
        Assert.Equal((0, 0), dm.ConnectionStats);
    }

    [Fact]
    public void DeviceManager_OneOffline_AllConnectedIsFalse()
    {
        var dm = new DeviceManager();
        dm.Register(new FakeDevice(true));
        dm.Register(new FakeDevice(false));
        Assert.False(dm.AllConnected);
        Assert.Equal((1, 2), dm.ConnectionStats);
    }

    [Fact]
    public void DeviceManager_AllOnline_AllConnectedIsTrue()
    {
        var dm = new DeviceManager();
        dm.Register(new FakeDevice(true));
        Assert.True(dm.AllConnected);
    }

    [Fact]
    public void DeviceManager_RegisterNull_Throws()
        => Assert.Throws<ArgumentNullException>(() => new DeviceManager().Register(null!));

    /// <summary>
    /// S2.4：连接必须由调用方显式触发，且单台失败只上报不抛。
    /// 旧实现里 ConnectAll 在 DI 工厂内被调用 —— 解析一个对象就等于开机连设备，
    /// 启动顺序被"谁先被解析"决定，测试一建容器还会去开真相机。
    /// </summary>
    [Fact]
    public void DeviceManager_ConnectAll_AllOnline_ReturnsNoFailure()
    {
        var dm = new DeviceManager();
        dm.Register(new FakeDevice(true));

        Assert.Empty(dm.ConnectAll());
    }

    [Fact]
    public void DeviceManager_ConnectAll_ReportsFailuresInsteadOfThrowing()
    {
        var dm = new DeviceManager();
        dm.Register(new FakeDevice(true));     // 连得上
        dm.Register(new FakeDevice(false));    // Connect 是空实现，调完仍离线 —— 必须被报出来
        dm.Register(new ThrowingDevice());     // Connect 抛异常 —— 不许带走其他设备

        var failures = dm.ConnectAll();

        // 两种失败都要能看见：抛异常的、以及"调了但没连上"的。
        // 只看异常会漏掉后者——相机打不开时各实现基本自己 catch 掉了。
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.Contains("boom"));
        Assert.Contains(failures, f => f.Contains("fake"));
    }

    [Fact]
    public void DeviceManager_ConnectAll_失败信息带出设备自己记录的原因()
    {
        // F2 修复的钉子：以前这里写"已调用 Connect 但仍未连接"——零信息量，
        // 自检再译成"有设备离线"，把"目录不存在/被独占"全引向"线没插好"。
        var offline = new FakeDevice(false) { LastError = "图片目录不存在: D:////nope" };
        var dm = new DeviceManager();
        dm.Register(offline);

        var failures = dm.ConnectAll();

        var line = Assert.Single(failures);
        Assert.Contains("图片目录不存在", line);
        Assert.DoesNotContain("已调用 Connect", line);
    }

    // ═══════════════════════════════════════════════════
    //  F-D10 触发回调异常不得带走进程
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task TimerTrigger_HandlerThrows_IsolatedAndKeepsTicking()
    {
        int calls = 0;
        Exception? reported = null;

        using var trigger = new TimerTrigger(20, ex => reported = ex);
        trigger.Triggered += _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("boom");
        };

        trigger.Start();
        await Task.Delay(400);
        trigger.Stop();

        // 旧实现：异常从 Elapsed 逃逸到 ThreadPool → 进程终止
        Assert.NotNull(reported);
        Assert.IsType<InvalidOperationException>(reported);
        // 且 _busy 不能被卡住（卡住 = 一次异常之后再也不触发）
        Assert.True(calls >= 2, $"处理器应被多次调用，实际 {calls} 次");
    }

    [Fact]
    public void TimerTrigger_IsRunning_TracksStartStop()
    {
        using var trigger = new TimerTrigger(1000);
        Assert.False(trigger.IsRunning);
        trigger.Start();
        Assert.True(trigger.IsRunning);
        trigger.Stop();
        Assert.False(trigger.IsRunning);
    }
}
