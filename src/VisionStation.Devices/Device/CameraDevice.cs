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

    /// <summary>失败原因直接来自底层相机实现（错误码/路径/文件名都在那儿）</summary>
    public string? LastError => _camera.LastError;

    public CameraDevice(ICamera camera) => _camera = camera;

    // 必须转发到 ICamera 的 open/close —— 旧实现只对 `_camera is FileCamera` 生效，
    // 而 DI 注入的是 SwappableCamera，判断永远为 false，界面上"重新连接"点了没有任何效果。
    public void Connect() => _camera.Open();

    public void Refresh() { /* 状态通过 ICamera 属性获取 */ }
    public void Disconnect() => _camera.Close();
}
