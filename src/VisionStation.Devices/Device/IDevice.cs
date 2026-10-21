namespace VisionStation.Devices;

/// <summary>
/// 设备统一接口 —— 产线上每个物理设备的抽象
///
/// 为什么需要？
///   产线软件不是只管相机——PLC、光源、传感器、扫码枪都是设备。
///   统一接口让 DeviceManager 能管所有设备（状态查询 + 连接/断开 + 健康检查）。
///
/// 当前实现：
///   CameraDevice  → 包装 ICamera
///   TriggerDevice → 包装 ITriggerSource
///   将来 PlcDevice / LightDevice / SensorDevice 都套这个接口
/// </summary>
public interface IDevice
{
    /// <summary>设备名称（显示在界面和日志里）</summary>
    string Name { get; }

    /// <summary>设备类型：Camera / PLC / Light / Sensor</summary>
    string Type { get; }

    /// <summary>是否在线</summary>
    bool IsConnected { get; }

    /// <summary>状态摘要（一行文字，如 "640x480@30fps · 已采集128帧"）</summary>
    string Status { get; }

    /// <summary>
    /// 最近一次失败的原因（人话，可直接展示）；null = 当前没有未恢复的失败。
    /// 自检/DeviceManager 用它替代"已调用 Connect 但仍未连接"/"有设备离线"这类没有信息量的句子。
    /// </summary>
    string? LastError { get; }

    /// <summary>连接设备</summary>
    void Connect();

    /// <summary>断开设备</summary>
    void Disconnect();
}
