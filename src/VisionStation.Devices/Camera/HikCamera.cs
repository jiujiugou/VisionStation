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
public class HikCamera : ICamera, IDisposable
{
    // ── 基础设施 ──
    private readonly ILogger<HikCamera>? _logger;   // 可空：相机在无 DI 场景也能 new
    private MyCamera? _camera;                      // SDK 设备句柄（Open 成功后才有意义）
    private bool _grabbing;                         // 采图线程运行开关（false = 线程退出）
    private int _frameCount;                        // 累计成功采集帧数（状态栏 FPS 参考）
    private Thread? _grabThread;                    // 后台采图线程（IsBackground，随进程退出）
    private IntPtr _imageBuffer = IntPtr.Zero;      // 预留的非托管缓冲（当前未使用，Dispose 里兜底释放）
    private readonly object _lock = new();          // 采图线程 ↔ Capture() 的互斥锁（保护下面那组帧字段）
    private bool _disposed;                         // Dispose 幂等保护：ProcessExit/OnExit/异常路径可能多次触发

    // ── 最新帧缓存（生产者-消费者核心）──
    // 采图线程写入 / Capture() 读取，全部在 _lock 内访问
    private byte[]? _latestFrame;    // 最新一帧的原始像素数据（格式由 enPixelType 决定）
    private int _latestWidth;        // 帧宽（px）
    private int _latestHeight;       // 帧高（px）
    private uint _latestPixelType;   // 像素格式枚举值（RGB24/Mono8...；⚠️ RawToMat 目前未按它分支，见该方法注释）
    private bool _hasNewFrame;       // 有新帧未消费的标志：Capture 取走后置 false，避免重复处理同一帧

    // ── 对外状态 ──
    public bool IsAvailable { get; private set; }   // 相机是否可用（Open+StartGrabbing 都成功才为 true）
    public int FrameCount => _frameCount;

    public HikCamera(ILogger<HikCamera>? logger = null)
    {
        _logger = logger;
        Initialize();
    }

    private void Initialize()
    {
        try
        {
            // ══ 步骤 1：SDK 全局初始化（整个进程只需要一次）══
            // 返回值非 0 = SDK 加载失败（DLL 缺失/版本不匹配），直接放弃
            int ret = MyCamera.MV_CC_Initialize_NET();
            if (ret != 0)
            {
                _logger?.LogWarning("[HikCamera] SDK初始化失败 错误码:{Code}", ret);
                return;
            }

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
                MyCamera.MV_CC_Finalize_NET();
                return;
            }

            _logger?.LogInformation("[HikCamera] 发现 {Count} 个设备", deviceList.nDeviceNum);

            // ══ 步骤 3：创建设备句柄（取第一个设备）══
            // pDeviceInfo 是非托管内存里的结构体，需要 Marshal 手动转
            var device = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(
                deviceList.pDeviceInfo[0], typeof(MyCamera.MV_CC_DEVICE_INFO))!;

            _camera = new MyCamera();
            ret = _camera.MV_CC_CreateDevice_NET(ref device);
            if (ret != 0) { _logger?.LogError("[HikCamera] 创建设备失败:{Code}", ret); return; }

            // ══ 步骤 4：打开设备（独占式）—— 最容易失败的一步 ══
            // 失败 0x80000203(-2147483133) = 无访问权限：
            //   上一个进程没释放（强杀后心跳会话未过期）或 MVS 客户端正占用。
            // 注意：失败路径必须 return，让 IsAvailable 保持 false，程序降级运行不崩溃
            ret = _camera.MV_CC_OpenDevice_NET();
            if (ret != 0) { _logger?.LogError("[HikCamera] 打开设备失败:{Code}", ret); return; }

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
            if (ret != 0) { _logger?.LogError("[HikCamera] 取流失败:{Code}", ret); return; }

            _grabbing = true;
            IsAvailable = true;

            // ══ 退出兜底：ProcessExit 在「正常退出」和「未处理异常终止」时都会触发 ══
            // 这样即使 App.OnExit 的容器 Dispose 因异常没走到，这里仍会释放设备。
            // 拦不住的只剩 taskkill /F / 断电 —— 那部分由上面缩短的心跳超时兜底。
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();

            // ══ 步骤 7：启动后台采图线程 ══
            // IsBackground = 进程退出时线程自动终止，不阻塞关闭
            _grabThread = new Thread(GrabLoop) { IsBackground = true };
            _grabThread.Start();

            _logger?.LogInformation("[HikCamera] 初始化成功");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[HikCamera] 初始化异常");
            IsAvailable = false;
        }
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
            int ret = _camera!.MV_CC_GetImageBuffer_NET(ref frameInfo, 500);

            if (ret == 0)
            {
                lock (_lock)
                {
                    int dataLen = (int)frameInfo.stFrameInfo.nFrameLen;
                    if (_latestFrame == null || _latestFrame.Length < dataLen)
                        _latestFrame = new byte[dataLen];

                    Marshal.Copy(frameInfo.pBufAddr, _latestFrame, 0, dataLen);
                    _latestWidth = (int)frameInfo.stFrameInfo.nWidth;
                    _latestHeight = (int)frameInfo.stFrameInfo.nHeight;
                    _latestPixelType = (uint)frameInfo.stFrameInfo.enPixelType;
                    _hasNewFrame = true;
                }

                _camera.MV_CC_FreeImageBuffer_NET(ref frameInfo);
            }
            else
            {
                Thread.Sleep(5);
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
            if (!_hasNewFrame || _latestFrame == null) return null;
            frameData = _latestFrame;
            w = _latestWidth;
            h = _latestHeight;
            pixelType = _latestPixelType;
            _hasNewFrame = false;
        }

        try
        {
            // 原始像素数据 → OpenCV Mat
            var mat = RawToMat(frameData, w, h, pixelType);
            if (mat == null || mat.Empty()) return null;

            _frameCount++;
            return new CameraFrame
            {
                Image = mat,
                Timestamp = DateTime.Now,
                FilePath = $"HikCamera_Frame_{_frameCount}"
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[HikCamera] 帧转换异常");
            return null;
        }
    }

    private static Mat? RawToMat(byte[] data, int width, int height, uint pixelType)
    {
        try
        {
            var mat = new Mat(height, width, MatType.CV_8UC3); // 默认 BGR
            Marshal.Copy(data, 0, mat.Data, data.Length);
            return mat;
        }
        catch
        {
            return null;
        }
    }

    public void open() => Initialize();
    public void close() => Dispose();

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

        _grabbing = false;
        _grabThread?.Join(2000);

        if (_camera != null)
        {
            _camera.MV_CC_StopGrabbing_NET();
            _camera.MV_CC_CloseDevice_NET();
            _camera.MV_CC_DestroyDevice_NET();
            _camera = null;
        }

        MyCamera.MV_CC_Finalize_NET();
        IsAvailable = false;

        if (_imageBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_imageBuffer);
            _imageBuffer = IntPtr.Zero;
        }
    }
}
