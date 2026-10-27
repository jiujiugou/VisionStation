using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.Services.Inspection;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 状态栏 ViewModel
///
/// 职责：
///   1. 订阅 ImageLinkStatusMessage → 更新图像链路在线状态
///   2. 订阅 PlcStatusMessage → 更新 PLC 灯（判断在顶栏，这里纯显示）
///   3. 订阅 VmThroughputMessage → 显示 VM 收帧 / 丢帧
///   4. 显示 PLC / 平台 / FPS / Cycle / VM 收丢 / OK / NG / CPU / 内存
///
/// 数据来源：四条消息。
///   - <see cref="ImageLinkStatusMessage"/> → <see cref="ImageLinkOnline"/>
///   - <see cref="PlcStatusMessage"/> → <see cref="PlcOnline"/>
///   - <see cref="InspectionResultMessage"/> → 累计 OK/NG、单帧耗时、FPS
///   - <see cref="VmThroughputMessage"/> → <see cref="VmFrameStats"/> / <see cref="HasVmDrops"/>
///
/// 累计值归本类自己维护（而不是由编排方算好塞进来）：它们是"显示统计"，
/// 谁显示谁记账，编排方不需要知道状态栏有哪几个数字。
/// </summary>
public partial class StatusBarViewModel : ObservableObject
{
    /// <summary>运行状态文字</summary>
    [ObservableProperty]
    private string _statusText = "就绪";

    /// <summary>
    /// PLC 是否在线。
    ///
    /// <para>
    /// 默认 false：程序刚起来、还没轮到第一次收发时，链路确实没验证过。
    /// 默认 true 等于挂一个假绿灯 —— 操作员会以为产线联动了。
    /// </para>
    /// <para>
    /// 数据来自 <see cref="PlcStatusMessage"/>（顶栏每帧检测后广播）。
    /// 之前这个属性<b>没有任何赋值点</b>，状态栏那个灯永远是灰的，
    /// 而顶栏那个是真的 —— 一真一假同时挂在界面上，操作员不知道该信哪个。
    /// 现在判断只在顶栏做一次，状态栏纯显示。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _plcOnline;

    /// <summary>
    /// 图像链路是否在线 —— 也就是"平台有没有在推结果"。
    ///
    /// 以前这里叫"相机在线"：程序自己采图时它反映相机状态。现在相机归平台独占，
    /// 本程序只收结果，再挂着"相机"两个字就是骗人；灯亮不亮只说明平台推没推。
    /// 默认 false 是诚实的：程序刚起来时一帧都还没收到。
    /// </summary>
    [ObservableProperty]
    private bool _imageLinkOnline;

    /// <summary>每秒处理帧数（实时吞吐，EMA 平滑，如 "0.5"；无帧时为 "--"）</summary>
    [ObservableProperty]
    private string _fps = "--";

    /// <summary>单帧处理周期（毫秒）</summary>
    [ObservableProperty]
    private string _cycleTime = "--";

    /// <summary>
    /// VM 结果链路的"收 N / 丢 M"；"--" = 还没收到过统计（VM 链路没启用时永远是 "--"）。
    ///
    /// <para>
    /// <b>为什么要显示</b>：丢帧意味着有一件<b>没被判过</b>，属于硬缺陷。以前这个数只写日志
    /// （每 5 帧一条 Warning），而操作员看的是界面 —— 处理跟不上（磁盘慢、库锁、单帧变慢）时，
    /// 界面一切正常，整批漏检要等良率异常才被发现。
    /// </para>
    /// <para>
    /// 默认 "--" 而不是 "收 0 / 丢 0"：VM 链路没启用时压根没有这条链路，
    /// 写 0/0 等于声称"连着、只是没来过数据"。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string _vmFrameStats = "--";

    /// <summary>
    /// 有没有丢过帧 —— 只用于状态栏那一格变红，不参与任何计算。
    ///
    /// 和文本分开是因为颜色是"有没有"的布尔判断，让 XAML 去解析数字串再比较会很脆。
    /// </summary>
    [ObservableProperty]
    private bool _hasVmDrops;

    /// <summary>累计 OK 数量</summary>
    [ObservableProperty]
    private int _okCount;

    /// <summary>累计 NG 数量</summary>
    [ObservableProperty]
    private int _ngCount;

    /// <summary>CPU 使用率</summary>
    [ObservableProperty]
    private string _cpuUsage = "--";

    /// <summary>内存占用</summary>
    [ObservableProperty]
    private string _memoryUsage = "--";

    /// <summary>单帧耗时的指数滑动平均，用来平滑 FPS（瞬时值抖动太大，看不出产线节拍）。</summary>
    private double _emaCycleMs;

    /// <summary>
    /// 构造即订阅。所有订阅都用弱引用重载，本类是单例，活到进程结束不反注册。
    ///
    /// <para>
    /// 计数口径是"本次运行累计"，不是今日 —— 今日良率在
    /// <see cref="ResultPanelViewModel"/> 里（那份会从库里补基数）。
    /// 状态栏这两个数字重启即归零，它衡量的是"这一轮跑得怎么样"，不是产量报表。
    /// </para>
    /// </summary>
    public StatusBarViewModel()
    {
        // 图像链路状态变化 → 更新在线指示
        WeakReferenceMessenger.Default.Register<StatusBarViewModel, ImageLinkStatusMessage>(this,
            static (vm, msg) => vm.ImageLinkOnline = msg.Value);

        // PLC 链路状态 → 顶栏是唯一判断点，这里只显示（见 PlcOnline 的说明）
        WeakReferenceMessenger.Default.Register<StatusBarViewModel, PlcStatusMessage>(this,
            static (vm, msg) => vm.PlcOnline = msg.Online);

        // VM 收帧 / 丢帧 → 纯显示。计数口径在驱动层（哪些帧算"到达"只留一处），
        // 这里只负责把它变成一句人话和一个"要不要变红"的标记。
        WeakReferenceMessenger.Default.Register<StatusBarViewModel, VmThroughputMessage>(this,
            static (vm, msg) =>
            {
                vm.VmFrameStats = $"收 {msg.Received} / 丢 {msg.Dropped}";
                vm.HasVmDrops = msg.Dropped > 0;
            });

        // 一帧完成 → 记账 + 刷新节拍。消息在 UI 线程上到达，不需要再切线程。
        WeakReferenceMessenger.Default.Register<StatusBarViewModel, InspectionResultMessage>(this,
            static (vm, msg) =>
            {
                var result = msg.Value;

                if (result.Report.IsOk) vm.OkCount++;
                else vm.NgCount++;

                vm.CycleTime = $"{result.CycleMs} ms";
                vm._emaCycleMs = vm._emaCycleMs == 0
                    ? result.CycleMs
                    : vm._emaCycleMs * 0.7 + result.CycleMs * 0.3;

                // CycleMs 为 0（极快的一帧或本地缓存命中）时不做除法，直接显示 "--"，
                // 否则会显示成 Infinity，看着像故障。
                vm.Fps = vm._emaCycleMs > 0 ? $"{1000.0 / vm._emaCycleMs:0.0}" : "--";
            });
    }
}
