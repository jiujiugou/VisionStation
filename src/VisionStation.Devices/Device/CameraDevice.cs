namespace VisionStation.Devices;

/// <summary>
/// 相机设备 —— 包装 ICamera，实现 IDevice
/// </summary>
public class CameraDevice : IDevice
{
    private readonly ICamera _camera;
    public string Name => "工业相机";
    public string Type => "Camera";
    public bool IsConnected => _camera.IsAvailable;

    public string Status => IsConnected
        ? $"就绪 · {_camera.FrameCount}帧"
        : "离线";

    public CameraDevice(ICamera camera) => _camera = camera;

    public void Connect()
    {
        if (_camera is FileCamera fc) fc.RefreshFileList();
    }

    public void Refresh() { /* 状态通过 ICamera 属性获取 */ }
    public void Disconnect() { }
}
