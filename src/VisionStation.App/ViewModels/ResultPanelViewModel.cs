using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 检测结果面板 ViewModel
///
/// 职责：
///   1. 订阅 InspectionResultMessage → 更新表格行
///   2. 显示每项检测的 OK/NG 状态和数值
///   3. 总判定条（绿色 OK / 红色 NG）
///
/// 不需要知道 Pipeline 怎么跑、相机谁管的——只关心收到的结果数据。
/// </summary>
public partial class ResultPanelViewModel : ObservableObject
{
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

    /// <summary>总判定背景色：#107C10(绿) 或 #E81123(红)</summary>
    [ObservableProperty]
    private string _verdictColor = "#888888";

    public ResultPanelViewModel()
    {
        // 收到检测结果 → 填充表格
        WeakReferenceMessenger.Default.Register<ResultPanelViewModel, InspectionResultMessage>(this,
            (vm, msg) =>
            {
                var r = msg.Value;

                vm.TimestampText = r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
                vm.TriggerInfoText = $"触发: {r.TriggerSource}";

                // 四行：特征有无 / 圆孔直径 / 划痕 / 缺口
                vm.Rows.Clear();
                var p = r.PresenceResult;
                vm.Rows.Add(new ResultRow
                {
                    Item = "特征有无",
                    IsOk = p.IsOk,
                    Value = p.Metrics.GetValueOrDefault("MatchScore", 0).ToString("F3")
                });

                var d = r.DimensionResult;
                vm.Rows.Add(new ResultRow
                {
                    Item = "圆孔直径",
                    IsOk = d.IsOk,
                    Value = $"{d.Metrics.GetValueOrDefault("Deviation_mm", 0):F2} mm"
                });

                var f = r.DefectResult;
                vm.Rows.Add(new ResultRow
                {
                    Item = "划痕",
                    IsOk = f.Metrics.GetValueOrDefault("ScratchCount", 0) == 0,
                    Value = $"{(int)f.Metrics.GetValueOrDefault("ScratchCount", 0)} 处"
                });
                vm.Rows.Add(new ResultRow
                {
                    Item = "缺口",
                    IsOk = f.Metrics.GetValueOrDefault("NotchCount", 0) == 0,
                    Value = $"{(int)f.Metrics.GetValueOrDefault("NotchCount", 0)} 处"
                });

                // 总判定条
                vm.VerdictText = r.OverallVerdict;
                vm.VerdictColor = r.IsOk ? "#107C10" : "#E81123";
            });
    }
}

/// <summary>
/// 检测结果表格的一行
/// </summary>
public class ResultRow
{
    /// <summary>检测项名称（特征有无 / 圆孔直径 / 划痕 / 缺口）</summary>
    public string Item { get; set; } = "";

    /// <summary>本项是否合格</summary>
    public bool IsOk { get; set; }

    /// <summary>显示用文字：OK 或 NG</summary>
    public string StatusText => IsOk ? "OK" : "NG";

    /// <summary>标签颜色：#107C10(绿) 或 #E81123(红)</summary>
    public string StatusColor => IsOk ? "#107C10" : "#E81123";

    /// <summary>测量数值（匹配度 / 偏差mm / 处数）</summary>
    public string Value { get; set; } = "";
}
