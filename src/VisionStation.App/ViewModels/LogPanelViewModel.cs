using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.Services.Inspection;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 日志面板 VM —— 支持按级别过滤。
///
/// 线程模型（W-09）：
///   - 日志可能来自任意线程（检测在线程池、UI 事件在 UI 线程），
///     所以只把条目塞进无锁队列，**不直接碰** <see cref="ObservableCollection{T}"/>；
///   - 由 UI 线程的一次 <c>BeginInvoke</c> 把队列里攒下的条目**一次性**灌进去，
///     每批只 <c>Refresh()</c> 一次。
///
/// 改造前是每条日志一次 <c>Dispatcher.Invoke</c>（阻塞等待 UI 线程）+ 一次 <c>Refresh()</c>：
/// UI 一忙，后台线程就陪着等；而 <c>Refresh()</c> 会重跑整个过滤视图，
/// 高频日志时面板本身就是主要卡顿来源。
///
/// 为什么不是固定 150ms 定时器：那需要在 UI 线程上常驻一个计时器，
/// 并在关闭时停掉它。用 Dispatcher 队列本身做合批已经能达到同样效果 ——
/// UI 忙时队列里攒得多（自动合并），UI 空闲时一条一批。
///
/// 为什么不用 <c>ICollectionView</c> 过滤：CollectionView 有 Dispatcher 亲和性，
/// 在哪个线程创建就只认那个线程，跨线程改源集合抛 NotSupportedException。
/// 而本类的日志可能来自任意线程 —— 用一个自维护的"可见列表"，
/// 过滤在单点做，线程问题上少一个变量，也顺带让这个类在无 UI 的测试里可用。
/// </summary>
public partial class LogPanelViewModel : ObservableObject
{
    /// <summary>
    /// 面板最多留多少条。200 是"够翻到刚才发生的事"与"内存不随运行时长增长"的折中：
    /// 超过就丢最旧的。**完整的日志在文件里**（Serilog 按天滚动保留 30 天），
    /// 面板只是让人当场看见，不是追溯工具。
    /// </summary>
    private const int MaxEntries = 200;

    /// <summary>全量日志（内部存储，最多 <see cref="MaxEntries"/> 条）</summary>
    private readonly ObservableCollection<LogEntry> _allEntries = new();

    /// <summary>待灌入 UI 的日志（任何线程都可以入队）</summary>
    private readonly ConcurrentQueue<LogEntry> _pending = new();

    /// <summary>0 = 没有排队的刷新，1 = 已经排了一次</summary>
    private int _flushScheduled;

    /// <summary>按当前级别过滤后、绑定到 View 的日志</summary>
    public ObservableCollection<LogEntry> VisibleEntries { get; } = new();

    /// <summary>当前过滤级别：只显示此级别及以上的日志</summary>
    [ObservableProperty]
    private LogMsgLevel _filterLevel = LogMsgLevel.Warning;

    /// <summary>过滤按钮文字</summary>
    [ObservableProperty]
    private string _filterLabel = "仅告警";

    /// <summary>
    /// 构造即订阅两路消息。两条都会走 <see cref="AddEntry"/>（无锁入队），
    /// 所以无论消息从哪个线程发来都安全。
    ///
    /// <para>
    /// 第二路把一帧判定也记一行：面板上要能连续看到"OK OK NG OK"，
    /// 而不是只有告警。否则一切正常时日志区一片空白，操作员会怀疑程序没在跑。
    /// </para>
    /// </summary>
    public LogPanelViewModel()
    {
        // 所有日志都存
        WeakReferenceMessenger.Default.Register<LogPanelViewModel, LogMessage>(this,
            static (vm, msg) => vm.AddEntry(msg.Value.Text, msg.Value.Level));

        // 检测结果也记
        WeakReferenceMessenger.Default.Register<LogPanelViewModel, InspectionResultMessage>(this,
            static (vm, msg) =>
            {
                var report = msg.Value.Report;
                vm.AddEntry(
                    $"{report.OverallVerdict} {string.Join(" ",
                        report.Results.Select(r => $"{r.Key}:{(r.Value.IsOk ? "OK" : "NG")}"))}",
                    report.IsOk ? LogMsgLevel.Success : LogMsgLevel.Error);
            });

        // 交接班清屏。注意清完之后控制面板还会再发一条"显示已清空"的日志，
        // 所以面板不会是一片空白 —— 空面板比一条提示更让人怀疑程序卡住了。
        WeakReferenceMessenger.Default.Register<LogPanelViewModel, ClearAllMessage>(this,
            static (vm, _) => vm.ClearEntries());
    }

    /// <summary>
    /// 清掉面板上的日志。**只清面板，不动文件日志**。
    ///
    /// <para>
    /// 文件日志（Serilog 按天滚动保留 30 天）才是追溯依据，这里清的是"当场看见的那些"。
    /// 所以这个动作不写审计、不可恢复，也不影响排障 —— 真要查那一分钟发生了什么，去看文件。
    /// </para>
    /// <para>
    /// <see cref="_pending"/> 也要清：不清的话，队列里还没灌进界面的那几十条
    /// 会在下一次 Flush 时又冒出来，"清空了但过一会儿自己长回来"是现场最不能接受的。
    /// </para>
    /// </summary>
    private void ClearEntries()
    {
        _pending.Clear();
        _allEntries.Clear();
        VisibleEntries.Clear();
    }

    /// <summary>入队一条日志。可以在任意线程调用，不阻塞。</summary>
    private void AddEntry(string text, LogMsgLevel level)
    {
        _pending.Enqueue(new LogEntry
        {
            Timestamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            Message = text,
            Level = level
        });

        ScheduleFlush();
    }

    /// <summary>
    /// 保证"至少排了一次刷新"。
    ///
    /// <para>
    /// <c>Interlocked.Exchange</c> 的作用是合批：队列里已经有 100 条待处理时，
    /// 第 101 条进来不会再排一次 BeginInvoke。标志在 <see cref="Flush"/> 开头清零，
    /// 所以下一次入队会重新排 —— 不会出现"标志卡在 1、之后再也不刷新"。
    /// </para>
    /// </summary>
    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 1) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // 没有 UI 线程（单元测试、控制台宿主）：没有合批的意义，就地灌进集合，
            // 否则断言"日志确实进了面板"会永远看不到内容。
            Flush();
            return;
        }

        if (dispatcher.HasShutdownStarted)
        {
            // UI 已经在拆了，再 BeginInvoke 会抛。此时面板马上消失，队列直接丢掉。
            _pending.Clear();
            return;
        }

        // Background 优先级：让绘制和输入先走，日志晚一帧不算问题。
        _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Flush));
    }

    /// <summary>把队列灌进集合，整批只刷新一次过滤视图。必须能在无 UI 的环境下工作。</summary>
    private void Flush()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);

        bool added = false;
        while (_pending.TryDequeue(out var entry))
        {
            _allEntries.Insert(0, entry);
            added = true;
        }

        while (_allEntries.Count > MaxEntries) _allEntries.RemoveAt(_allEntries.Count - 1);

        if (added) ApplyFilter();
    }

    /// <summary>按当前级别重建可见列表。一次刷新只做一遍，不做逐条过滤。</summary>
    private void ApplyFilter()
    {
        VisibleEntries.Clear();
        foreach (var entry in _allEntries)
        {
            if (entry.Level >= FilterLevel) VisibleEntries.Add(entry);
        }
    }

    /// <summary>
    /// 关窗时把还没灌进面板的日志刷掉。
    /// 不刷的话，关闭前最后几条（"手动停止""正在关闭"）只存在于文件日志里，界面上看不到。
    /// </summary>
    public void FlushPending()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Flush);
            return;
        }

        Flush();
    }

    /// <summary>
    /// 过滤级别变了：先更新按钮文字，再重建可见列表。
    ///
    /// <para>
    /// 顺序不能反 —— <see cref="ApplyFilter"/> 读的是 <see cref="FilterLevel"/>，
    /// 而按钮文字只是展示。反过来的话会先用新文字配旧列表，闪一下。
    /// </para>
    /// </summary>
    partial void OnFilterLevelChanged(LogMsgLevel value)
    {
        FilterLabel = value switch
        {
            LogMsgLevel.Info => "全部",
            LogMsgLevel.Success => "成功以上",
            LogMsgLevel.Warning => "仅告警",
            LogMsgLevel.Error => "仅错误",
            _ => "自定义"
        };
        ApplyFilter();
    }

    /// <summary>切换过滤级别：仅告警 → 全部 → 仅错误 → 仅告警</summary>
    [RelayCommand]
    private void ToggleFilter()
    {
        FilterLevel = FilterLevel switch
        {
            LogMsgLevel.Warning => LogMsgLevel.Info,
            LogMsgLevel.Info => LogMsgLevel.Error,
            LogMsgLevel.Error => LogMsgLevel.Warning,
            _ => LogMsgLevel.Warning
        };
    }
}

/// <summary>
/// 日志面板里的一行。**不可变内容 + 公有的 set**：属性是简单数据，
/// 面板只读不写；用 class 而不是 record 是因为 WPF 绑定列表项时
/// record 的相等语义会让"内容相同"的两行被判成同一项，导致列表刷新异常。
/// </summary>
public class LogEntry
{
    /// <summary>本地时间 HH:mm:ss。不带日期 —— 面板只显示当天，日期在文件日志里。</summary>
    public string Timestamp { get; set; } = "";

    /// <summary>正文。可能是中文提示，也可能是异常消息。</summary>
    public string Message { get; set; } = "";

    /// <summary>级别。决定文字颜色与整行底色（见 Converters）。</summary>
    public LogMsgLevel Level { get; set; } = LogMsgLevel.Info;
}
