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

    public ResultPanelViewModel()
    {
        WeakReferenceMessenger.Default.Register<ResultPanelViewModel, InspectionResultMessage>(this,
            static (vm, msg) => vm.Apply(msg.Value));
    }

    /// <summary>把一帧报告摊平成表格行。调用方保证在 UI 线程上（消息由页面 VM 在 UI 线程发出）。</summary>
    private void Apply(InspectionRunResult result)
    {
        var report = result.Report;

        TimestampText = report.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
        TriggerInfoText = $"触发: {report.TriggerSource}";

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
