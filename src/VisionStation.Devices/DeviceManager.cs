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

    /// <summary>
    /// 连接所有设备。返回连接失败的信息（空 = 全部成功）。
    ///
    /// 必须由调用方在"启动流程"里显式调用，不能塞进 DI 工厂：
    /// 工厂里 Connect 等于"谁先解析谁触发设备连接"，启动顺序变成解析顺序的副作用，
    /// 而且测试一构造容器就会去开真相机。
    ///
    /// 失败判据不能只看异常：各实现的 Connect 内部基本都自己 catch（相机打不开是常态，
    /// 不该把进程带走），所以还要在 Connect 之后查一次 IsConnected。
    /// </summary>
    public IReadOnlyList<string> ConnectAll()
    {
        var failures = new List<string>();

        foreach (var d in _devices)
        {
            try
            {
                d.Connect();
            }
            catch (Exception ex)
            {
                failures.Add($"{d.Name}: {ex.GetType().Name} {ex.Message}");
                continue;
            }

            if (!d.IsConnected)
                failures.Add($"{d.Name}: 已调用 Connect 但仍未连接");
        }

        return failures;
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
