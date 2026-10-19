namespace VisionStation.Devices;

/// <summary>
/// 设备管理器 —— 统一管理所有 IDevice
///
/// 职责：
///   1. 持有所有已注册设备
///   2. 启动时全量连接
///   3. 提供设备列表给状态栏和自检
/// </summary>
public class DeviceManager
{
    private readonly List<IDevice> _devices = new();

    /// <summary>所有已注册设备</summary>
    public IReadOnlyList<IDevice> Devices => _devices;

    /// <summary>
    /// 所有设备是否全部在线。
    /// 空表必须判 false：List.All 对空集合恒为 true，"0/0 报全部在线"是恒真判据
    /// —— DI 装配漏注册时界面会说一切正常，而实际一台设备都没有。
    /// </summary>
    public bool AllConnected => _devices.Count > 0 && _devices.All(d => d.IsConnected);

    /// <summary>在线设备数 / 总数</summary>
    public (int online, int total) ConnectionStats =>
        (_devices.Count(d => d.IsConnected), _devices.Count);

    /// <summary>注册设备</summary>
    public void Register(IDevice device)
    {
        if (device is null) throw new ArgumentNullException(nameof(device));
        _devices.Add(device);
    }

    /// <summary>连接所有设备</summary>
    public void ConnectAll()
    {
        foreach (var d in _devices)
        {
            try { d.Connect(); }
            catch { /* 单个失败不影响其他 */ }
        }
    }

    /// <summary>断开所有设备</summary>
    public void DisconnectAll()
    {
        foreach (var d in _devices)
        {
            try { d.Disconnect(); }
            catch { }
        }
    }
}
