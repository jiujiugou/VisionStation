using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 日志面板 VM —— 支持按级别过滤
/// </summary>
public partial class LogPanelViewModel : ObservableObject
{
    private const int MaxEntries = 200;

    /// <summary>全量日志（内部存储）</summary>
    private readonly ObservableCollection<LogEntry> _allEntries = new();

    /// <summary>过滤后的日志（绑定到 View）</summary>
    public ICollectionView Entries { get; }

    /// <summary>当前过滤级别：只显示此级别及以上的日志</summary>
    [ObservableProperty]
    private LogMsgLevel _filterLevel = LogMsgLevel.Warning;

    /// <summary>过滤按钮文字</summary>
    [ObservableProperty]
    private string _filterLabel = "仅告警";

    public LogPanelViewModel()
    {
        Entries = CollectionViewSource.GetDefaultView(_allEntries);
        Entries.Filter = FilterEntry;

        // 所有日志都存
        WeakReferenceMessenger.Default.Register<LogPanelViewModel, LogMessage>(this,
            (vm, msg) => AddEntry(msg.Value.Text, msg.Value.Level));

        // 检测结果也记
        WeakReferenceMessenger.Default.Register<LogPanelViewModel, InspectionResultMessage>(this,
            (vm, msg) =>
            {
                var r = msg.Value;
                AddEntry(
                    $"{r.OverallVerdict} 有无:{r.PresenceResult.IsOk} 尺寸:{r.DimensionResult.IsOk} 缺陷:{r.DefectResult.IsOk}",
                    r.IsOk ? LogMsgLevel.Success : LogMsgLevel.Error);
            });
    }

    private void AddEntry(string text, LogMsgLevel level)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _allEntries.Insert(0, new LogEntry
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                Message = text,
                Level = level
            });
            while (_allEntries.Count > MaxEntries) _allEntries.RemoveAt(_allEntries.Count - 1);
            Entries.Refresh(); // 通知 View 刷新过滤
        });
    }

    /// <summary>过滤逻辑：低于 FilterLevel 的不显示</summary>
    private bool FilterEntry(object obj)
    {
        if (obj is LogEntry entry)
            return entry.Level >= FilterLevel;
        return false;
    }

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
        Entries.Refresh();
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

public class LogEntry
{
    public string Timestamp { get; set; } = "";
    public string Message { get; set; } = "";
    public LogMsgLevel Level { get; set; } = LogMsgLevel.Info;
}
