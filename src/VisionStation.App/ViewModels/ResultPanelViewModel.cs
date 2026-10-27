using System.Globalization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.Services.Inspection;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 检测结果面板 ViewModel
///
/// 职责：
///   1. 订阅 <see cref="InspectionResultMessage"/> → 生成表格行
///   2. 每行只给"名字 / 是否合格 / 数值"三个语义值，**不给颜色**
///
/// 为什么不再写死四行：改造前这里把 Presence / Dimension / Defect 三个工具
/// 硬拆成"特征有无 / 圆孔直径 / 划痕 / 缺口"四行（划痕和缺口都是从同一条
/// Defect 结果里按 Metric 名抠出来的）。配方里加一个工具、或者把缺陷工具去掉，
/// 界面就会缺行或多行，而代码看不出来 —— 这是 W-11。
///
/// 现在遍历报告里的工具结果：一行一个工具，值直接用工具自己的 Summary
/// （如"设计:10.00mm 实测:10.05mm 偏差:0.05mm"）。
/// 名字映射只是**显示标签**，未知工具原样显示工具名，不会丢行。
///
/// 颜色也不在这里：VM 只输出 <see cref="ResultRow.IsOk"/> / <see cref="IsOk"/>，
/// 红或绿由 XAML 的 DataTrigger 决定，换主题只改 XAML。
/// </summary>
public partial class ResultPanelViewModel : ObservableObject
{
    /// <summary>
    /// 工具名 → 界面显示名。认不出来的工具直接显示原名。
    ///
    /// <para>
    /// <b>这三个键已经不会在运行时出现了</b>：Presence / Dimension / Defect 属于
    /// 2026-10 删除的自研算法引擎。VM 模式下报告里只有一条
    /// <c>ResultKeys.Engine</c>（VM 的判定摘要），会走"未知工具原样显示"的分支，
    /// 所以表格里显示的是工具名本身。
    /// </para>
    /// <para>
    /// 保留这张表是为了读历史记录：旧数据里存着这三个键名，翻出来时仍能显示成中文。
    /// 真要在界面上看到中文标签，应当由引擎在 <c>Summary</c> 里给出业务名，而不是靠这张表。
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> ToolLabels = new(StringComparer.Ordinal)
    {
        ["Presence"] = "特征有无",
        ["Dimension"] = "圆孔直径",
        ["Defect"] = "缺陷",
    };

    /// <summary>检测时间</summary>
    [ObservableProperty]
    private string _timestampText = "--";

    /// <summary>触发来源 + 帧号</summary>
    [ObservableProperty]
    private string _triggerInfoText = "--";

    /// <summary>表格数据行：检测项 | 结果 | 数值</summary>
    public ObservableCollection<ResultRow> Rows { get; } = new();

    /// <summary>总判定文字：OK 或 NG</summary>
    [ObservableProperty]
    private string _verdictText = "--";

    /// <summary>总判定是否合格（颜色由 XAML 的 DataTrigger 决定）</summary>
    [ObservableProperty]
    private bool _isOk;

    // ═══════════════════════════════════════════════════════════
    //  运行指标（2026-10-24 加入，同日改为"今日"口径）
    //
    //  口径是 **今天 0 点到现在**，不是"本次运行"。
    //  以前纯内存计数，班中重启一次就归零，产线上没人能解释良率为什么从 99% 掉到 98%，
    //  也最容易把这种假象当成"换配方后质量变差了"。
    //  现在启动时用 <see cref="Seed"/> 从库里补基数，之后每帧继续内存累加。
    //
    //  为什么不每帧查库：一次检测到上屏要 4~12ms，再叠一次 SQLite 聚合查询，
    //  高节拍下会把 UI 线程拖成瓶颈。基数查一次 + 内存累加，重启语义正确、开销可忽略。
    // ═══════════════════════════════════════════════════════════

    /// <summary>今日累计判定数（启动时从库补基数）</summary>
    [ObservableProperty]
    private int _totalCount;

    /// <summary>今日累计 NG 数</summary>
    [ObservableProperty]
    private int _ngCount;

    /// <summary>今日良率（百分比字符串；无判定时为 "--"）</summary>
    [ObservableProperty]
    private string _okRateText = "--";

    /// <summary>判定不合格时的原因摘要（取第一个不合格项的名字）</summary>
    [ObservableProperty]
    private string _verdictSubText = "";

    /// <summary>最近一条需要人处理的告警（来自 LogMessage 的 Warning / Error）</summary>
    [ObservableProperty]
    private string _recentIssueText = "";

    /// <summary>是否有未处理的告警（控制告警卡的显隐）</summary>
    [ObservableProperty]
    private bool _hasIssue;

    /// <summary>最近一帧的单帧耗时（含采集、检测、存图）</summary>
    [ObservableProperty]
    private string _lastCycleText = "--";

    /// <summary>
    /// 是否已经收到过至少一帧判定。
    ///
    /// 没有它的话，界面刚启动时 <see cref="IsOk"/> 是 false，会显示成一个大红 NG ——
    /// "还没检测"被画成"检测不合格"，是产线上最不能接受的一类误导。
    /// </summary>
    [ObservableProperty]
    private bool _hasVerdict;

    /// <summary>
    /// 构造即订阅两条消息：检测结果（摊平成表格）、日志（挑出 Warning/Error 上图）。
    ///
    /// <para>
    /// 本类是单例、活到进程结束，所以不反注册。两条订阅都用弱引用重载 ——
    /// 消息中心不会把 VM 钉住（虽然这里是单例，钉住也无所谓，但保持全项目一致）。
    /// </para>
    /// </summary>
    public ResultPanelViewModel()
    {
        WeakReferenceMessenger.Default.Register<ResultPanelViewModel, InspectionResultMessage>(this,
            static (vm, msg) => vm.Apply(msg.Value));

        // 告警卡只收 Warning / Error —— Info 和 Success 是流水账，堆在判定旁边会淹没真问题。
        // 这是"报警分级"里最粗的一刀：需要人处理的才上图。
        WeakReferenceMessenger.Default.Register<ResultPanelViewModel, LogMessage>(this,
            static (vm, msg) =>
            {
                if (msg.Value.Level is not (LogMsgLevel.Warning or LogMsgLevel.Error)) return;

                vm.RecentIssueText = $"{DateTime.Now:HH:mm}  {msg.Value.Text}";
                vm.HasIssue = true;
            });

        // 交接班清屏（控制面板的「清空」）
        WeakReferenceMessenger.Default.Register<ResultPanelViewModel, ClearAllMessage>(this,
            static (vm, _) => vm.ClearDisplay());
    }

    /// <summary>
    /// 清掉眼前显示的东西，<b>不动今日统计</b>。
    ///
    /// <para>
    /// 清的是：明细表、上一帧的结论与耗时、告警卡。
    /// 不清的是：<see cref="TotalCount"/> / <see cref="NgCount"/> / <see cref="OkRateText"/> ——
    /// 那是"今天 0 点到现在"的口径（启动时从库补基数，见上面的字段注释）。
    /// 把它们归零会让良率从 97% 突然变成 "--"，交接班的人会以为质量出事了，
    /// 而实际上只是有人按了下清屏。要重置统计得另外做一个明确的动作，不能挂在清屏上。
    /// </para>
    /// </summary>
    private void ClearDisplay()
    {
        Rows.Clear();

        HasVerdict = false;
        VerdictText = "--";
        VerdictSubText = "";
        IsOk = false;
        LastCycleText = "--";

        RecentIssueText = "";
        HasIssue = false;
    }

    /// <summary>把一帧报告摊平成表格行。调用方保证在 UI 线程上（消息由页面 VM 在 UI 线程发出）。</summary>
    private void Apply(InspectionRunResult result)
    {
        var report = result.Report;

        TimestampText = report.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        TriggerInfoText = $"触发: {report.TriggerSource}";
        LastCycleText = $"{result.CycleMs} ms";

        Rows.Clear();
        foreach (var (toolName, tool) in report.Results)
        {
            Rows.Add(new ResultRow
            {
                Item = ToolLabels.TryGetValue(toolName, out var label) ? label : toolName,
                IsOk = tool.IsOk,
                Value = tool.Summary
            });
        }

        VerdictText = report.OverallVerdict;
        IsOk = report.IsOk;
        HasVerdict = true;

        // 指标：今日口径。启动时 Seed 补过基数，这里只做增量累加。
        TotalCount++;
        if (!report.IsOk) NgCount++;

        UpdateOkRate();

        // 判 NG 时把"为什么"写在判定下面：操作员看到大字是 NG，
        // 紧接着要知道是哪一项出的问题，不该再低头到表格里逐行找红标。
        VerdictSubText = report.IsOk
            ? "全部检测项合格"
            : Rows.FirstOrDefault(row => !row.IsOk)?.Item ?? "存在不合格项";
    }

    /// <summary>
    /// 用库里今日已有的统计补基数。**必须在 UI 线程调用**（会触发属性通知）。
    ///
    /// 为什么需要它：良率以前是纯内存计数，班中重启一次就归零。
    /// 产线上这个数字是要给班长看的，归零比不准更糟 —— 它会让人误判成"换配方之后质量掉了"。
    ///
    /// 口径说明：**今天 0 点到现在**（自然日）。三班倒的现场若跨 0 点，
    /// 这里要改成按班次起点算，属于业务参数，不能由代码替现场决定。
    /// </summary>
    public void Seed(int okCount, int ngCount)
    {
        if (okCount < 0 || ngCount < 0) return;

        TotalCount = okCount + ngCount;
        NgCount = ngCount;
        UpdateOkRate();
    }

    /// <summary>
    /// 重算良率文字。分母用 <see cref="TotalCount"/>（含 OK 与 NG），
    /// 无判定时显示 "--" 而不是 "0%" —— 0% 会被读成"全不合格"。
    /// </summary>
    private void UpdateOkRate() =>
        OkRateText = TotalCount > 0
            ? $"{100.0 * (TotalCount - NgCount) / TotalCount:0.0}%"
            : "--";
}

/// <summary>
/// 检测结果表格的一行
/// </summary>
public class ResultRow
{
    /// <summary>检测项名称（工具名或它的显示名）</summary>
    public string Item { get; set; } = "";

    /// <summary>本项是否合格</summary>
    public bool IsOk { get; set; }

    /// <summary>显示用文字：OK 或 NG</summary>
    public string StatusText => IsOk ? "OK" : "NG";

    /// <summary>工具给的一行摘要（数值 + 判定依据）</summary>
    public string Value { get; set; } = "";
}
