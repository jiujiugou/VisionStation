using OpenCvSharp;
using VisionStation.Core.Models;
using VisionStation.Devices;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// 设备层（U08-U15 / E05-E11）的三步法验证用例。
///
/// 覆盖重点不是"功能能不能用"，而是失效被登记时声称的那条不变量：
///   - 可用性判据必须能被反证（拔线/耗尽后不能再报在线）
///   - 热切换失败必须回滚（不能"新的没建成、旧的先拆了"）
///   - 连接/断开必须真的转发到被包装对象
///   - 后台线程异常不能带走进程
/// </summary>
public class DeviceLayerTests
{
    // ═══════════════════════════════════════════════════
    //  替身
    // ═══════════════════════════════════════════════════

    private sealed class FakeCamera : ICamera
    {
        public bool Available { get; set; } = true;

        /// <summary>open() 之后是否变可用 —— 用来模拟"工厂只造对象、连接后才可用"的真实相机</summary>
        public bool AvailableAfterOpen { get; set; }

        public int OpenCount { get; private set; }
        public int CloseCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool IsAvailable => Available;
        public int FrameCount => 0;
        public void open()
        {
            OpenCount++;
            if (AvailableAfterOpen) Available = true;
        }
        public void close() => CloseCount++;
        public CameraFrame? Capture() => null;
        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeTrigger : ITriggerSource
    {
        public bool IsRunning { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public event Action? Triggered;
        public void Start() { IsRunning = true; StartCount++; }
        public void Stop() { IsRunning = false; StopCount++; }
        public void Fire() => Triggered?.Invoke();

        // 接口继承 IDisposable 后必须实现；替身无资源，空实现即可
        public void Dispose() => GC.SuppressFinalize(this);
    }

    private sealed class FakeDevice : IDevice
    {
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
        public string Name => "boom";
        public string Type => "Fake";
        public bool IsConnected => false;
        public string Status => "离线";
        public void Connect() => throw new InvalidOperationException("设备被占用");
        public void Disconnect() { }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vsdev_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════
    //  F-D02 像素格式：不认识的格式必须明确失败，不能按字节数猜通道
    // ═══════════════════════════════════════════════════

    [Fact]
    public void TryConvertPixels_Mono8_ReturnsSingleChannel()
    {
        using var mat = HikCamera.TryConvertPixels(new byte[4 * 3], 4, 3, out string? err);
        Assert.Null(err);
        Assert.NotNull(mat);
        Assert.Equal(1, mat!.Channels());
        Assert.Equal(4, mat.Width);
        Assert.Equal(3, mat.Height);
    }

    [Fact]
    public void TryConvertPixels_Rgb8_ReturnsThreeChannel()
    {
        using var mat = HikCamera.TryConvertPixels(new byte[4 * 3 * 3], 4, 3, out string? err);
        Assert.Null(err);
        Assert.NotNull(mat);
        Assert.Equal(3, mat!.Channels());
    }

    [Fact]
    public void TryConvertPixels_Mono16_Rejected()
    {
        // 2 字节/像素：旧实现按 8UC1 处理，把高位字节当像素值 → 图像全错但流程照跑
        var mat = HikCamera.TryConvertPixels(new byte[4 * 3 * 2], 4, 3, out string? err);
        Assert.Null(mat);
        Assert.Contains("Mono16", err);
    }

    [Fact]
    public void TryConvertPixels_Bgra8_Rejected()
    {
        var mat = HikCamera.TryConvertPixels(new byte[4 * 3 * 4], 4, 3, out string? err);
        Assert.Null(mat);
        Assert.NotNull(err);
    }

    [Fact]
    public void TryConvertPixels_ShortBuffer_Rejected()
    {
        var mat = HikCamera.TryConvertPixels(new byte[5], 4, 3, out string? err);
        Assert.Null(mat);
        Assert.Contains("数据不足", err);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(4, 0)]
    [InlineData(-1, 3)]
    public void TryConvertPixels_InvalidSize_Rejected(int w, int h)
    {
        var mat = HikCamera.TryConvertPixels(new byte[100], w, h, out string? err);
        Assert.Null(mat);
        Assert.Contains("尺寸非法", err);
    }

    // ═══════════════════════════════════════════════════
    //  F-D01 帧心跳：IsAvailable 必须能被反证
    // ═══════════════════════════════════════════════════

    [Fact]
    public void FrameAlive_NeverReceived_IsFalse()
        => Assert.False(HikCamera.FrameAlive(0, 10_000, 3000));

    [Fact]
    public void FrameAlive_RecentFrame_IsTrue()
        => Assert.True(HikCamera.FrameAlive(9_000, 10_000, 3000));

    [Fact]
    public void FrameAlive_NoFrameForLongerThanThreshold_IsFalse()
    {
        // 相机掉线：最后一次出帧在 4 秒前，阈值 3 秒 → 必须判不可用
        Assert.False(HikCamera.FrameAlive(6_000, 10_000, 3000));
    }

    // ═══════════════════════════════════════════════════
    //  F-D06 热切换：新模式不可用必须回滚
    // ═══════════════════════════════════════════════════

    [Fact]
    public void SwapTo_UnavailableCandidate_RollsBackAndKeepsOldCamera()
    {
        var old = new FakeCamera { Available = true };
        var bad = new FakeCamera { Available = false };   // 模拟"旧海康还占着设备，新的打不开"
        using var swappable = new SwappableCamera(old, _ => bad, "Hikvision");

        bool ok = swappable.SwapTo("Hikvision");

        Assert.False(ok);
        Assert.True(swappable.IsAvailable);     // 仍委托给旧相机（旧相机可用，新相机不可用）
        Assert.Equal("Hikvision", swappable.CurrentMode);
        Assert.Equal(0, old.DisposeCount);      // 旧相机绝不能被拆
        Assert.Equal(1, bad.DisposeCount);      // 建不成的那个要清掉
    }

    [Fact]
    public void SwapTo_AvailableCandidate_SwitchesAndDisposesOld()
    {
        var old = new FakeCamera { Available = true };
        var fresh = new FakeCamera { Available = true };
        using var swappable = new SwappableCamera(old, _ => fresh, "File");

        bool ok = swappable.SwapTo("Hikvision");

        Assert.True(ok);
        Assert.Equal("Hikvision", swappable.CurrentMode);
        Assert.True(swappable.IsAvailable);
        Assert.Equal(1, old.DisposeCount);
    }

    /// <summary>
    /// S2.5：工厂只造对象、不连接，所以"连上"必须由 SwapTo 负责。
    /// 候选相机构造后不可用、open 之后才可用 —— 如果 SwapTo 不调 open，
    /// 这条会失败（切换永远回滚），正是改造前真实相机的行为。
    /// </summary>
    [Fact]
    public void SwapTo_OpensCandidate_BecauseFactoryOnlyCreates()
    {
        var old = new FakeCamera { Available = true };
        var candidate = new FakeCamera { Available = false, AvailableAfterOpen = true };
        using var swappable = new SwappableCamera(old, _ => candidate, "File");

        bool ok = swappable.SwapTo("Hikvision");

        Assert.True(ok);
        Assert.Equal(1, candidate.OpenCount);
        Assert.True(swappable.IsAvailable);
        Assert.Equal("Hikvision", swappable.CurrentMode);
        Assert.Equal(1, old.DisposeCount);
    }

    [Fact]
    public void SwapTo_FactoryThrows_RollsBack()
    {
        var old = new FakeCamera { Available = true };
        using var swappable = new SwappableCamera(old, _ => throw new InvalidOperationException("no sdk"), "File");

        Assert.False(swappable.SwapTo("Hikvision"));
        Assert.Equal("File", swappable.CurrentMode);
        Assert.Equal(0, old.DisposeCount);
    }

    [Fact]
    public void CurrentMode_FromExplicitInitialMode_NotHardcodedFile()
    {
        // 配置 Mode=Hikvision 时，旧实现把 CurrentMode 写死成 "File"，
        // 于是 UI 的"同模式不重复切"守卫失效，一次误点就会把正在用的相机拆掉。
        using var swappable = new SwappableCamera(new FakeCamera(), _ => new FakeCamera(), "Hikvision");
        Assert.Equal("Hikvision", swappable.CurrentMode);
    }

    // ═══════════════════════════════════════════════════
    //  F-D09 连接/断开必须真的转发
    // ═══════════════════════════════════════════════════

    [Fact]
    public void SwappableCamera_Open_ForwardsToActive()
    {
        var cam = new FakeCamera();
        using var swappable = new SwappableCamera(cam, _ => new FakeCamera(), "File");
        swappable.open();
        Assert.Equal(1, cam.OpenCount);
    }

    [Fact]
    public void SwappableCamera_Close_ForwardsToActive()
    {
        var cam = new FakeCamera();
        using var swappable = new SwappableCamera(cam, _ => new FakeCamera(), "File");
        swappable.close();
        Assert.Equal(1, cam.CloseCount);
    }

    [Fact]
    public void CameraDevice_Connect_ForwardsToOpen_EvenWhenNotFileCamera()
    {
        // 旧实现写的是 `if (_camera is FileCamera fc)`，而 DI 注入的是 SwappableCamera，
        // 判断永远为 false —— 界面上"重新连接"点了没有任何效果。
        var cam = new FakeCamera();
        var dev = new CameraDevice(cam);
        dev.Connect();
        Assert.Equal(1, cam.OpenCount);
    }

    [Fact]
    public void CameraDevice_Disconnect_ForwardsToClose()
    {
        var cam = new FakeCamera();
        var dev = new CameraDevice(cam);
        dev.Disconnect();
        Assert.Equal(1, cam.CloseCount);
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

    // ═══════════════════════════════════════════════════
    //  F-D07 / F-D08 / F-D01(File) 文件相机
    // ═══════════════════════════════════════════════════

    [Fact]
    public void FileCamera_Construct_DoesNotScanDisk()
    {
        using var dir = new TempDir();
        using (var img = new Mat(4, 4, MatType.CV_8UC1, new Scalar(10)))
            Cv2.ImWrite(Path.Combine(dir.Path, "a.png"), img);

        var cam = new FileCamera(dir.Path, "*.png");

        // S2.5：构造函数只赋值。以前这里会在 new 的瞬间扫磁盘 —— DI 装配、
        // 热切换的候选实例、单元测试都会顺手触发一次真实 IO。
        Assert.Equal(0, cam.TotalFiles);
        Assert.False(cam.IsAvailable);

        // open() 才是"连接"
        cam.open();
        Assert.Equal(1, cam.TotalFiles);
        Assert.True(cam.IsAvailable);
    }

    [Fact]
    public void FileCamera_MissingFolder_DoesNotThrowAndIsUnavailable()
    {
        // 目录被删/路径打错是现场常态；构造函数不许碰磁盘（S2.5），open() 里也不许抛
        var cam = new FileCamera(Path.Combine(Path.GetTempPath(), "vs_missing_" + Guid.NewGuid().ToString("N")));

        // 构造只赋值：没 open 之前一律不可用（不是"构造即就绪"）
        Assert.False(cam.IsAvailable);
        Assert.Null(cam.Capture());

        cam.open();
        Assert.False(cam.IsAvailable);
        Assert.Equal(0, cam.TotalFiles);

        // 重复 open 是重连语义，不能抛
        cam.RefreshFileList();
        Assert.Equal(0, cam.TotalFiles);
    }

    [Fact]
    public void FileCamera_NonLoopExhausted_BecomesUnavailable()
    {
        using var dir = new TempDir();
        foreach (var name in new[] { "a.png", "b.png" })
            using (var img = new Mat(4, 4, MatType.CV_8UC1, new Scalar(128)))
                Cv2.ImWrite(Path.Combine(dir.Path, name), img);

        var cam = new FileCamera(dir.Path, "*.png", loop: false);
        cam.open();     // S2.5：连接语义在 open，不再靠构造函数
        Assert.True(cam.IsAvailable);

        for (int i = 0; i < 2; i++)
        {
            var frame = cam.Capture();
            Assert.NotNull(frame);
            frame!.Image.Dispose();
        }

        // 读完之后：不能再报"就绪"，否则界面说在线、采集永远返回 null
        Assert.False(cam.IsAvailable);
        Assert.Null(cam.Capture());
    }

    [Fact]
    public void FileCamera_LoopMode_StaysAvailableAfterWrapAround()
    {
        using var dir = new TempDir();
        using (var img = new Mat(4, 4, MatType.CV_8UC1, new Scalar(200)))
            Cv2.ImWrite(Path.Combine(dir.Path, "only.png"), img);

        var cam = new FileCamera(dir.Path, "*.png", loop: true);
        cam.open();
        for (int i = 0; i < 3; i++)
        {
            var frame = cam.Capture();
            Assert.NotNull(frame);
            frame!.Image.Dispose();
        }

        Assert.True(cam.IsAvailable);
    }

    [Fact]
    public async Task FileCamera_ConcurrentRefreshAndCapture_NoException()
    {
        // 刷新（UI 线程）与采集（检测线程）并发：旧实现在原地 _files.Clear()，
        // 采集线程手里的列表会在计数检查之后被清空 → ArgumentOutOfRangeException
        using var dir = new TempDir();
        using (var img = new Mat(8, 8, MatType.CV_8UC1, new Scalar(90)))
            Cv2.ImWrite(Path.Combine(dir.Path, "x.png"), img);

        var cam = new FileCamera(dir.Path, "*.png", loop: true);
        cam.open();
        Exception? failure = null;
        int stop = 0;

        var reader = Task.Run(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                try
                {
                    cam.Capture()?.Image.Dispose();
                }
                catch (Exception ex)
                {
                    failure = ex;
                    return;
                }
            }
        });

        for (int i = 0; i < 300; i++) cam.RefreshFileList();

        Volatile.Write(ref stop, 1);
        var finished = await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(10))) == reader;
        Assert.True(finished, "采集线程未在 10s 内结束");
        await reader;
        Assert.Null(failure);
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
        trigger.Triggered += () =>
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
