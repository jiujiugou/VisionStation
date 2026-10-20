using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MvCamCtrl.NET;
using OpenCvSharp;
using VisionStation.Core.Models;

namespace VisionStation.Devices;

/// <summary>
/// 海康工业相机 — 实现 ICamera 接口
/// 基于 MVS SDK V4.x (MvCameraControl.Net.dll)
///
/// ══ 连接七步链路（Initialize）══
///   1. MV_CC_Initialize_NET     SDK 全局初始化（进程一次）
///   2. MV_CC_EnumDevices_NET    枚举设备（GigE | USB 双协议）
///   3. MV_CC_CreateDevice_NET   创建设备句柄
///   4. MV_CC_OpenDevice_NET     打开设备 ← 最容易失败的一步
///   5. SetEnumValue             连续采集 + 关硬件触发
///   6. MV_CC_StartGrabbing_NET  开始取流
///   7. 启动 GrabLoop 后台线程   持续缓存最新帧
///
/// ══ 打开设备失败的排查经验（0x80000203 = MV_E_ACCESS_DENIED）══
///   现象：枚举得到设备，OpenDevice 却报 -2147483133。
///   原因：设备被独占——上一个进程异常退出（强杀/崩溃）后心跳会话未释放，
///         或者 MVS 客户端正连接着该设备（谁连接谁独占）。
///   恢复：等心跳超时；MVS 虚拟相机需在虚拟设备工具里删除重加（重置会话）。
///   根治：程序退出路径必须释放设备（见 App.OnExit 的容器 Dispose → 本类 Dispose）。
/// </summary>
public class HikCamera : ICamera
{
    // ── 基础设施 ──
    private readonly ILogger<HikCamera>? _logger;   // 可空：相机在无 DI 场景也能 new
    private MyCamera? _camera;                      // SDK 设备句柄（Open 成功后才有意义）
    private volatile bool _grabbing;                // 采图线程运行开关（false = 线程退出；volatile 保证采图线程能看见）
    private int _frameCount;                        // 累计成功采集帧数（状态栏 FPS 参考）
    private Thread? _grabThread;                    // 后台采图线程（IsBackground，随进程退出）
    private IntPtr _imageBuffer = IntPtr.Zero;      // 预留的非托管缓冲（当前未使用，Dispose 里兜底释放）
    private readonly object _lock = new();          // 采图线程 ↔ Capture() 的互斥锁（保护下面那组帧字段）
    private volatile bool _disposed;                // Dispose 幂等保护：ProcessExit/OnExit/异常路径可能多次触发
    private bool _available;                        // 开流成功且未判定失联（IsAvailable 的"真"位）
    private long _lastFrameTick;                    // 最近一次成功取帧的 Environment.TickCount64
    private bool _sdkHeld;                          // 本实例是否持有进程级 SDK 引用计数
    private static int _sdkRefs;                    // 进程级 SDK 引用计数：多实例并存时只有最后一个 Finalize

    // ── 最新帧缓存（生产者-消费者核心）──
    // 采图线程写入 / Capture() 读取，全部在 _lock 内访问
    private byte[]? _latestFrame;    // 最新一帧的原始像素数据（格式由 enPixelType 决定）
    private int _latestLen;          // 本帧真实字节数（_latestFrame 会被复用，长度可能大于本帧）
    private int _latestWidth;        // 帧宽（px）
    private int _latestHeight;       // 帧高（px）
    private uint _latestPixelType;   // 像素格式枚举值（RGB24/Mono8...）
    private bool _hasNewFrame;       // 有新帧未消费的标志：Capture 取走后置 false，避免重复处理同一帧

    // ── 对外状态 ──
    /// <summary>超过此时长没有新帧即判定失联（毫秒）</summary>
    public int StaleAfterMs { get; set; } = 3000;

    /// <summary>
    /// 相机是否可用：开流成功 **且** 最近 StaleAfterMs 内出过帧。
    /// 只置真不置假会让"在线"变成恒真判据 —— 拔网线/掉电后界面仍显示就绪，
    /// 而 Capture() 永远返回 null，检测静默停止且没有任何线索。
    /// </summary>
    public bool IsAvailable => _available && FrameAlive(_lastFrameTick, Environment.TickCount64, StaleAfterMs);

    /// <summary>帧心跳判据（纯函数，便于单测）：从未出帧 = 不可用；超过阈值无新帧 = 不可用</summary>
    public static bool FrameAlive(long lastFrameMs, long nowMs, int staleAfterMs)
        => lastFrameMs > 0 && staleAfterMs > 0 && nowMs >= lastFrameMs && nowMs - lastFrameMs <= staleAfterMs;

    public int FrameCount => _frameCount;

    /// <remarks>
    /// 只赋值，不连接（W-01/W-02 / S2.5）。
    /// 构造即连接有两个实打实的问题：热切换会先造候选实例，候选必然因为旧实例还占着设备
    /// 而 open 失败；测试和 DI 装配里 new 一下就会去抢物理相机。
    /// 连接统一走 Open()。
    /// </remarks>
    public HikCamera(ILogger<HikCamera>? logger = null)
    {
        _logger = logger;
    }

    private void Initialize()
    {
        try
        {
            // ══ 步骤 1：SDK 全局初始化（进程级引用计数，多实例并存时只有最后一个才 Finalize）══
            // 返回值非 0 = SDK 加载失败（DLL 缺失/版本不匹配），直接放弃
            int ret = SdkAcquire();
            if (ret != 0)
            {
                _logger?.LogWarning("[HikCamera] SDK初始化失败 错误码:{Code}", ret);
                return;
            }
            _sdkHeld = true;

            // ══ 步骤 2：枚举设备（GigE 网口 | USB 双协议同时扫）══
            // MVS 虚拟相机也会在这里被扫到；扫不到 = 相机没电/没接线/驱动没装
            var deviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
            ret = MyCamera.MV_CC_EnumDevices_NET(
                MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE,
                ref deviceList);

            if (ret != 0 || deviceList.nDeviceNum == 0)
            {
                _logger?.LogWarning("[HikCamera] 未发现相机 错误码:{Code} 数量:{Count}",
                    ret, deviceList.nDeviceNum);
                ReleaseSdk();
                return;
            }

            _logger?.LogInformation("[HikCamera] 发现 {Count} 个设备", deviceList.nDeviceNum);

            // ══ 步骤 3：创建设备句柄（取第一个设备）══
            // pDeviceInfo 是非托管内存里的结构体，需要 Marshal 手动转
            var device = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(
                deviceList.pDeviceInfo[0], typeof(MyCamera.MV_CC_DEVICE_INFO))!;

            _camera = new MyCamera();
            ret = _camera.MV_CC_CreateDevice_NET(ref device);
            if (ret != 0)
            {
                _logger?.LogError("[HikCamera] 创建设备失败:{Code}", ret);
                _camera = null;
                ReleaseSdk();
                return;
            }

            // ══ 步骤 4：打开设备（独占式）—— 最容易失败的一步 ══
            // 失败 0x80000203(-2147483133) = 无访问权限：
            //   上一个进程没释放（强杀后心跳会话未过期）或 MVS 客户端正占用。
            // 失败路径必须销毁步骤3已创建的句柄 —— 否则句柄与设备侧会话一直挂着，
            // 下次启动继续报 0x80000203，正是本类注释里那条"心跳会话未释放"的成因之一。
            ret = _camera.MV_CC_OpenDevice_NET();
            if (ret != 0)
            {
                _logger?.LogError("[HikCamera] 打开设备失败:{Code}", ret);
                TeardownDevice();
                ReleaseSdk();
                return;
            }

            // ══ 步骤 4.5：缩短心跳超时（GigE 相机有效，USB/虚拟相机忽略）══
            // 作用：本进程若异常死亡（崩溃/强杀），设备侧会在超时后自动释放独占会话。
            // SDK 默认可能等 60s+；设为 3s，重启程序几乎不用等。
            // 设置失败不阻塞（USB 相机无此节点，忽略即可）
            ret = _camera.MV_CC_SetIntValue_NET("GevHeartbeatTimeout", 3000);
            if (ret != 0)
                _logger?.LogInformation("[HikCamera] 心跳超时设置不支持(USB/虚拟相机忽略) 错误码:{Code}", ret);

            // ══ 步骤 5：配置采集模式 ══
            // 连续采集（自由触发）；TriggerMode OFF = 相机自己不停出帧，
            // 硬件触发模式留给将来 PlcTrigger 对接（外触发拍照）
            _camera.MV_CC_SetEnumValue_NET("AcquisitionMode",
                (uint)MyCamera.MV_CAM_ACQUISITION_MODE.MV_ACQ_MODE_CONTINUOUS);
            _camera.MV_CC_SetEnumValue_NET("TriggerMode",
                (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);

            // ══ 步骤 6：开始取流 ══
            ret = _camera.MV_CC_StartGrabbing_NET();
            if (ret != 0)
            {
                _logger?.LogError("[HikCamera] 取流失败:{Code}", ret);
                TeardownDevice();
                ReleaseSdk();
                return;
            }

            _grabbing = true;
            _available = true;
            _lastFrameTick = Environment.TickCount64;   // 从此刻起算心跳，首帧到达后由采图线程刷新

            // ══ 退出兜底：ProcessExit 在「正常退出」和「未处理异常终止」时都会触发 ══
            // 这样即使 App.OnExit 的容器 Dispose 因异常没走到，这里仍会释放设备。
            // 拦不住的只剩 taskkill /F / 断电 —— 那部分由上面缩短的心跳超时兜底。
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            // ══ 步骤 7：启动后台采图线程 ══
            // IsBackground = 进程退出时线程自动终止，不阻塞关闭
            _grabThread = new Thread(GrabLoop) { IsBackground = true };
            _grabThread.Start();

            _logger?.LogInformation("[HikCamera] 初始化成功");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[HikCamera] 初始化异常");
            _available = false;
        }
    }

    private void OnProcessExit(object? sender, EventArgs e) => Dispose();

    /// <summary>
    /// 取进程级 SDK 引用：第一个实例负责 Initialize，后续实例只计数。
    /// MV_CC_Finalize_NET 是进程级操作，多实例各调一次会把别的实例的取流一起废掉
    /// （热切换时新旧实例必然短暂并存）。
    /// </summary>
    private static int SdkAcquire()
    {
        if (Interlocked.Increment(ref _sdkRefs) > 1) return 0;
        int ret = MyCamera.MV_CC_Initialize_NET();
        if (ret != 0) Interlocked.Decrement(ref _sdkRefs);
        return ret;
    }

    private void ReleaseSdk()
    {
        if (!_sdkHeld) return;
        _sdkHeld = false;
        if (Interlocked.Decrement(ref _sdkRefs) > 0) return;
        Interlocked.Exchange(ref _sdkRefs, 0);
        MyCamera.MV_CC_Finalize_NET();
    }

    /// <summary>销毁设备句柄（失败路径与 Dispose 共用）——建了就要毁，不能靠 GC</summary>
    private void TeardownDevice()
    {
        if (_camera == null) return;
        try { _camera.MV_CC_StopGrabbing_NET(); } catch { /* 未开流时调用会报错，忽略 */ }
        try { _camera.MV_CC_CloseDevice_NET(); } catch { /* 未打开时调用会报错，忽略 */ }
        try { _camera.MV_CC_DestroyDevice_NET(); } catch { /* 句柄可能已失效，忽略 */ }
        _camera = null;
    }

    /// <summary>
    /// 后台采图线程：持续从相机取帧到缓存
    ///
    /// 设计：生产者-消费者模型
    ///   生产者（本线程）—— 不停取 SDK 内部帧 → 拷到 _latestFrame（覆盖旧帧）
    ///   消费者（Capture）—— 检测时来取"最新一帧"，采图与检测解耦
    ///   相机永远在流，检测按需取，互不阻塞。
    ///
    /// GetImageBuffer 的 500 是超时(ms)：超时返回非 0 → sleep(5) 防空转烧 CPU
    /// 取到帧必须 FreeImageBuffer 归还 SDK，否则 SDK 内部缓冲耗尽后不再出帧
    /// </summary>
    private void GrabLoop()
    {
        while (_grabbing)
        {
            var frameInfo = new MyCamera.MV_FRAME_OUT();
            int ret;
            try
            {
                var cam = _camera;
                if (cam == null) break;          // 已被 Dispose 摘空
                ret = cam.MV_CC_GetImageBuffer_NET(ref frameInfo, 500);
            }
            catch (Exception ex)
            {
                // 后台线程的未捕获异常会直接终止进程（.NET 默认行为），必须就地拦下
                _logger?.LogError(ex, "[HikCamera] 取帧异常，采图线程退出");
                break;
            }

            if (ret != 0)
            {
                Thread.Sleep(5);
                continue;
            }

            try
            {
                lock (_lock)
                {
                    int dataLen = (int)frameInfo.stFrameInfo.nFrameLen;
                    if (dataLen <= 0)
                        throw new InvalidOperationException($"SDK 返回的帧长度为 {dataLen}，丢弃该帧");
                    if (_latestFrame == null || _latestFrame.Length < dataLen)
                        _latestFrame = new byte[dataLen];

                    Marshal.Copy(frameInfo.pBufAddr, _latestFrame, 0, dataLen);
                    _latestLen = dataLen;
                    _latestWidth = (int)frameInfo.stFrameInfo.nWidth;
                    _latestHeight = (int)frameInfo.stFrameInfo.nHeight;
                    _latestPixelType = (uint)frameInfo.stFrameInfo.enPixelType;
                    _hasNewFrame = true;
                    _lastFrameTick = Environment.TickCount64;   // 心跳：IsAvailable 的判据
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[HikCamera] 帧拷贝异常（已丢弃该帧）");
            }
            finally
            {
                // 无论拷贝是否失败都必须归还 SDK 缓冲：不归还 = SDK 内部缓冲耗尽 = 相机从此不出帧
                try { _camera?.MV_CC_FreeImageBuffer_NET(ref frameInfo); }
                catch (Exception ex) { _logger?.LogError(ex, "[HikCamera] 归还帧缓冲失败"); }
            }
        }
    }

    public CameraFrame? Capture()
    {
        if (!IsAvailable || _camera == null) return null;

        byte[]? frameData;
        int w, h;
        uint pixelType;

        lock (_lock)
        {
            if (!_hasNewFrame || _latestFrame == null || _latestLen <= 0) return null;

            // ★ 必须拷贝出快照，不能直接持有 _latestFrame 引用：
            //   GrabLoop 下一帧会在锁内 Marshal.Copy 覆盖同一个数组，
            //   而下面的 RawToMat 在锁外读它 —— 直接引用会拿到"上半帧旧、下半帧新"的拼接鬼影，
            //   检测结果随机错乱且无任何报错，是最难查的一类现场故障。
            frameData = new byte[_latestLen];
            Buffer.BlockCopy(_latestFrame, 0, frameData, 0, _latestLen);

            w = _latestWidth;
            h = _latestHeight;
            pixelType = _latestPixelType;
            _hasNewFrame = false;
        }

        try
        {
            // 原始像素数据 → OpenCV Mat
            var mat = TryConvertPixels(frameData, w, h, out string? why);
            if (mat == null)
            {
                // 格式不认识就明确失败 + 留痕，不能按字节数猜一个通道数把错图喂给检测
                if (why != null)
                    _logger?.LogError("[HikCamera] 帧转换失败: {Why}（pixelType=0x{Pixel:X8}）", why, pixelType);
                return null;
            }
            if (mat.Empty()) { mat.Dispose(); return null; }

            int n = Interlocked.Increment(ref _frameCount);
            return new CameraFrame
            {
                Image = mat,
                Timestamp = DateTime.Now,
                FilePath = $"HikCamera_Frame_{n}"
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[HikCamera] 帧转换异常");
            return null;
        }
    }

    /// <summary>
    /// 原始像素缓冲 → OpenCV Mat。
    ///
    /// 只接受能确定解释的"每像素字节数"：1 = Mono8，3 = RGB/BGR8。
    /// 其余一律返回 null 并给出原因 —— 宁可这一帧丢掉并报错，也不能按字节数猜一个通道数：
    ///   Mono16 是 2 字节/像素，按 8UC1 解释会把高位字节当成像素值，图像尺寸/通道数全合法、
    ///   数值全错，检测照常跑，属于"静默错误判定"，现场根本发现不了。
    ///   4 字节（BGRA/Mono32）同理，旧实现按 8UC3 拷 pixels*3 字节，行跨直接错位。
    ///
    /// 该方法是纯函数（不碰 SDK），单测直接覆盖 1/2/3/4 字节与非法尺寸。
    /// </summary>
    public static Mat? TryConvertPixels(byte[]? data, int width, int height, out string? error)
    {
        error = null;
        if (data == null) { error = "帧数据为空"; return null; }
        if (width <= 0 || height <= 0) { error = $"尺寸非法 {width}x{height}"; return null; }

        long pixels = (long)width * height;
        if (data.LongLength < pixels) { error = $"数据不足 {data.LongLength} < {pixels}"; return null; }

        int bytesPerPixel = (int)(data.LongLength / pixels);
        MatType type;
        int channels;
        switch (bytesPerPixel)
        {
            case 1: type = MatType.CV_8UC1; channels = 1; break;
            case 3: type = MatType.CV_8UC3; channels = 3; break;
            case 2: error = "Mono16 未支持（需右移 8 位降位深）"; return null;
            case 4: error = "4 字节像素（BGRA/Mono32）未支持"; return null;
            default: error = $"未知像素宽度 {bytesPerPixel} 字节/像素"; return null;
        }

        try
        {
            var mat = new Mat(height, width, type);
            // 只拷 Mat 装得下的量：绝不越界，也不把上一帧残留的尾巴拷进来
            Marshal.Copy(data, 0, mat.Data, (int)(pixels * channels));
            return mat;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public void Open()
    {
        // 重新连接：Dispose 过的实例必须复位幂等标志，否则再次 Dispose 会直接 return，
        // 设备会话永远不释放 —— 下次启动就是 0x80000203。
        _disposed = false;
        if (_camera != null) TeardownDevice();
        Initialize();
    }

    public void Close() => Dispose();

    /// <summary>
    /// 释放设备 —— 退出路径必须走这里，否则会话挂死，下次启动报 0x80000203 占用
    /// 顺序有讲究：先停线程（不再取帧）→ 停取流 → 关设备 → 销毁句柄 → SDK 反初始化，
    /// 反着来或漏一步都会让 SDK/相机侧残留状态。
    /// 由 App.OnExit → DI 容器 Dispose 自动调用。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;   // 幂等：OnExit/ProcessExit/异常路径可能重复触发
        _disposed = true;
        _available = false;

        _grabbing = false;
        var thread = _grabThread;
        bool threadStopped = thread == null || thread.Join(2000);
        if (!threadStopped)
        {
            // 线程还卡在 SDK 里取帧：这时销毁句柄/反初始化 SDK，它下一次调用就落在已释放的
            // 句柄上 —— 原生访问违例不可捕获，整个进程直接没。宁可漏掉这次释放交给心跳超时兜底。
            _logger?.LogError("[HikCamera] 采图线程 2s 未退出，跳过设备销毁与 SDK 反初始化");
            return;
        }

        TeardownDevice();
        ReleaseSdk();

        if (_imageBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_imageBuffer);
            _imageBuffer = IntPtr.Zero;
        }
    }
}
