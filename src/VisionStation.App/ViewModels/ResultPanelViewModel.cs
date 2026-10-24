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
    /// <summary>工具名 → 界面显示名。认不出来的工具直接显示原名。</summary>
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
    //  运行指标（2026-10-24 加入）
    //
    //  归本面板自己记账，不去问 StatusBarViewModel 要 ——
    //  状态栏那三个数是"整机吞吐"，这里是"这一屏看过的判定"，
    //  两者口径不同，共用一个来源迟早对不上。
    // ═══════════════════════════════════════════════════════════

    /// <summary>本次运行累计判定数</summary>
    [ObservableProperty]
    private int _totalCount;

    /// <summary>本次运行累计 NG 数</summary>
    [ObservableProperty]
    private int _ngCount;

    /// <summary>良率（百分比字符串；无判定时为 "--"）</summary>
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

        // 指标：谁显示谁记账。只统计"这一屏真的收到过的判定"，
        // 不冒充"今日产量" —— 那个数在数据库里，得按班次查，不能靠内存计数凑。
        TotalCount++;
        if (!report.IsOk) NgCount++;

        OkRateText = TotalCount > 0
            ? $"{100.0 * (TotalCount - NgCount) / TotalCount:0.0}%"
            : "--";

        // 判 NG 时把"为什么"写在判定下面：操作员看到大字是 NG，
        // 紧接着要知道是哪一项出的问题，不该再低头到表格里逐行找红标。
        VerdictSubText = report.IsOk
            ? "全部检测项合格"
            : Rows.FirstOrDefault(row => !row.IsOk)?.Item ?? "存在不合格项";
    }
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
