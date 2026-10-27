namespace VisionStation.Devices;

/// <summary>
/// 设备管理器 —— 统一管理所有 IDevice
///
/// <para>
/// <b>定位</b>：设备注册表 + 启动时的一次性连接动作。它不是"设备守护进程"：
/// 不轮询、不重连、不发事件，也不会自己发现设备。谁把它解析出来，谁就看到当前快照。
/// </para>
///
/// <para>
/// <b>归属</b>：组合根注册为 DI 单例（<c>AddDevices</c>），创建时只是 <c>Register</c> 触发源，**不连接**；
/// 真正的连接发生在 <c>AppHost.InitializeAsync</c> 里显式调 <see cref="ConnectAll"/>，
/// 顺序必须在自检之前（自检读 <see cref="ConnectionStats"/>）。
/// 目前表里只有一项（<see cref="TriggerDevice"/>），见 <see cref="IDevice"/> 关于"抽象数=1"的说明。
/// </para>
///
/// <para>
/// <b>线程约束</b>：本类**没有任何同步**。<see cref="Register"/> 与遍历都默认在启动线程（UI 线程）
/// 上串行发生；运行期没有代码再往表里加东西，所以读侧（自检、状态栏）与写侧不会真正撞上。
/// 若将来要做热插拔（运行中注册/移除设备），这里必须换成并发容器并重审遍历期间的快照语义。
/// </para>
///
/// <para>
/// <b>失败行为</b>：<see cref="ConnectAll"/> 不抛，把每台设备的失败原因收集成字符串返回；
/// <see cref="DisconnectAll"/> 逐个吞掉异常。设计前提是"一台设备缺席不该让产线看不见界面"。
/// </para>
/// </summary>
public class DeviceManager
{
    private readonly List<IDevice> _devices = new();

    /// <summary>所有已注册设备。**返回的是内部列表本身**（只读视图），调用方按约定不要长时间持有。</summary>
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

    /// <summary>
    /// 注册设备。
    /// <para>
    /// 只允许在启动装配阶段调用一次。重复注册同一实例会让"在线/总数"里的总数虚高，
    /// 而自检只比较 <c>online == total</c>，所以不会报错 —— 只是分母悄悄错了。
    /// </para>
    /// </summary>
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
    /// 而且测试一构造容器就会去开真串口 / 建真 TCP 连接。
    ///
    /// 失败判据不能只看异常：各实现的 Connect 内部基本都自己 catch（串口被别的程序占了、
    /// 网线没插都是常态，不该把进程带走），所以还要在 Connect 之后查一次 IsConnected。
    ///
    /// <para>
    /// <b>阻塞提醒</b>：本方法同步执行，每台设备的 Connect 都可能耗到自己的连接超时
    /// （TCP 2s / 串口打开失败通常更快）。它是在启动流程的 UI 线程上调用的，
    /// 所以"PLC 地址填错"的现场表现是**启动时窗口晚出现约 2 秒**。
    /// 目前设备只有一台，这个代价可以接受；设备变多时要挪到后台线程去做。
    /// </para>
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
            {
                // LastError 里是实现类自己记的原因（串口名/波特率/IP/错误码）。
                // 以前这里写"已调用 Connect 但仍未连接"——零信息量，
                // 自检再译成"有设备离线"，把"串口被独占/波特率不对"全引向"线没插好"。
                failures.Add($"{d.Name}: {d.LastError ?? "已调用 Connect 但仍未连接（实现未记录原因）"}");
            }
        }

        return failures;
    }

    /// <summary>
    /// 断开所有设备。
    ///
    /// <para>
    /// <b>当前没有调用方</b>：关闭流程不调它。
    /// </para>
    /// <para>
    /// ⚠ <b>而且关闭流程其实也没有别的地方在关 PLC 连接</b>（只记录，未修）：
    /// <c>IPlcConnection</c> 是在组合根的工厂 lambda 里 new 出来的，**没有注册进容器**，
    /// 所以容器释放时不会碰它；<c>PlcTriggerSource.Dispose</c> 和 <c>PlcResultWriter.Dispose</c>
    /// 又都明确声明"不关连接"。结果就是串口/socket 一直开到进程退出，由操作系统回收。
    /// 进程级资源这样收尾不算错误（不会泄漏增长），但如果将来要做"关窗后重启设备"或
    /// "同一进程内切换 PLC 配置"，必须先补一个明确的 owner 来 Dispose 它 ——
    /// 否则第二个实例会打不开同一个 COM 口。
    /// </para>
    /// <para>
    /// 之所以还留着这个方法，是因为"管理器能停掉自己管的东西"是这个类的基本承诺，
    /// 而且 <c>TriggerDevice.Disconnect</c> 里那段"记住断开前是否在跑"的逻辑需要有调用点才能被验证（单测在用它）。
    /// </para>
    /// <para>
    /// 逐个吞异常：收尾路径上一个设备关不掉，不能让后面的设备留在打开状态。
    /// </para>
    /// </summary>
    public void DisconnectAll()
    {
        foreach (var d in _devices)
        {
            try { d.Disconnect(); }
            catch { }
        }
    }
}
