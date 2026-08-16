using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MvCamCtrl.NET;
using OpenCvSharp;
using VisionStation.Core.Models;

namespace VisionStation.Devices;

/// <summary>
/// 海康工业相机 — 实现 ICamera 接口
/// 基于 MVS SDK V4.x (MvCameraControl.Net.dll)
/// </summary>
public class HikCamera : ICamera, IDisposable
{
    private readonly ILogger<HikCamera>? _logger;
    private MyCamera? _camera;
    private bool _grabbing;
    private int _frameCount;
    private Thread? _grabThread;
    private IntPtr _imageBuffer = IntPtr.Zero;
    private readonly object _lock = new();

    // 最新帧数据（由采图线程写入，Capture() 读取）
    private byte[]? _latestFrame;
    private int _latestWidth;
    private int _latestHeight;
    private uint _latestPixelType;
    private bool _hasNewFrame;

    public bool IsAvailable { get; private set; }
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
            // 1. 初始化 SDK
            int ret = MyCamera.MV_CC_Initialize_NET();
            if (ret != 0)
            {
                _logger?.LogWarning("[HikCamera] SDK初始化失败 错误码:{Code}", ret);
                return;
            }

            // 2. 枚举设备
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

            // 3. 打开第一个设备
            var device = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(
                deviceList.pDeviceInfo[0], typeof(MyCamera.MV_CC_DEVICE_INFO))!;

            _camera = new MyCamera();
            ret = _camera.MV_CC_CreateDevice_NET(ref device);
            if (ret != 0) { _logger?.LogError("[HikCamera] 创建设备失败:{Code}", ret); return; }

            ret = _camera.MV_CC_OpenDevice_NET();
            if (ret != 0) { _logger?.LogError("[HikCamera] 打开设备失败:{Code}", ret); return; }

            // 4. 设置连续采集 + 关闭触发
            _camera.MV_CC_SetEnumValue_NET("AcquisitionMode",
                (uint)MyCamera.MV_CAM_ACQUISITION_MODE.MV_ACQ_MODE_CONTINUOUS);
            _camera.MV_CC_SetEnumValue_NET("TriggerMode",
                (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);

            // 5. 开始取流
            ret = _camera.MV_CC_StartGrabbing_NET();
            if (ret != 0) { _logger?.LogError("[HikCamera] 取流失败:{Code}", ret); return; }

            _grabbing = true;
            IsAvailable = true;

            // 6. 启动采图线程
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

    /// <summary>后台采图线程：持续从相机取帧到缓存</summary>
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

    public void Dispose()
    {
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
