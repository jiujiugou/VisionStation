using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 侧边导航栏 ViewModel
///
/// 职责：
///   1. 管理导航项（上半部：检测/参数/配方/历史，下半部：相机/PLC/日志/系统）
///   2. 订阅 TriggerStateMessage → 自动模式暗化、停止时恢复
///   3. 绿色脉冲指示灯只在自动运行时显示（View 层绑定 IsDimmed）
/// </summary>
public partial class SidebarViewModel : ObservableObject
{
    /// <summary>当前选中的导航页</summary>
    [ObservableProperty]
    private string _activePage = "检测";

    /// <summary>是否暗化（自动运行时 true，手动/停止时 false）</summary>
    [ObservableProperty]
    private bool _isDimmed;

    /// <summary>上部导航项</summary>
    public List<NavItem> TopItems { get; } = new()
    {
        new("检测", "M12 2C6.5 2 2 6.5 2 12s4.5 10 10 10 10-4.5 10-10S17.5 2 12 2zm-2 15l-5-5 1.4-1.4 3.6 3.6 7.6-7.6L19 8l-9 9z"),
        new("参数", "M19.14 12.94c.04-.3.06-.61.06-.94 0-.33-.02-.64-.06-.94l2.02-1.58a.49.49 0 00.12-.61l-1.92-3.32a.49.49 0 00-.59-.22l-2.39.96a7.04 7.04 0 00-1.62-.94l-.36-2.54a.48.48 0 00-.48-.41h-3.84a.48.48 0 00-.48.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96a.49.49 0 00-.59.22L2.74 8.87a.48.48 0 00.12.61l2.02 1.58c-.04.3-.06.61-.06.94s.02.64.06.94l-2.02 1.58a.49.49 0 00-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.37 1.03.7 1.62.94l.36 2.54c.05.24.26.41.48.41h3.84c.24 0 .44-.17.48-.41l.36-2.54c.59-.24 1.13-.57 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.02-1.58zM12 15.6A3.6 3.6 0 1115.6 12 3.61 3.61 0 0112 15.6z"),
        new("配方", "M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8l-6-6zm-1 2l5 5h-5V4zM8 14h8v2H8v-2zm0 4h5v2H8v-2z"),
        new("历史", "M13 3a9 9 0 00-9 9H1l3.9 3.9L8.8 12H6c0-3.9 3.1-7 7-7s7 3.1 7 7-3.1 7-7 7c-1.9 0-3.7-.8-5-2l-1.4 1.4C8.3 20 10 21 12 21a9 9 0 000-18zm-1 5v5l4.3 2.5.7-1.2-3.5-2.1V8H12z"),
    };

    /// <summary>下部导航项</summary>
    public List<NavItem> BottomItems { get; } = new()
    {
        new("相机", "M9 2L7.2 4H4a2 2 0 00-2 2v12a2 2 0 002 2h16a2 2 0 002-2V6a2 2 0 00-2-2h-3.2L15 2H9zm3 15a5 5 0 110-10 5 5 0 010 10z M12 8.8a3.2 3.2 0 100 6.4 3.2 3.2 0 000-6.4z"),
        new("PLC",   "M4 3h16a2 2 0 012 2v10a2 2 0 01-2 2H4a2 2 0 01-2-2V5a2 2 0 012-2zm2 4a1.5 1.5 0 100 3 1.5 1.5 0 000-3zm4 0a1.5 1.5 0 100 3 1.5 1.5 0 000-3zm4 0a1.5 1.5 0 100 3 1.5 1.5 0 000-3zm-8 6h4v3H6v-3zm8 0h4v3h-4v-3z"),
        new("日志", "M20 2H4a2 2 0 00-2 2v18l4-4h14a2 2 0 002-2V4a2 2 0 00-2-2zm-7 9h-2V5h2v6zm-2 4h2v-2h-2v2z"),
        new("系统", "M12 15a3 3 0 100-6 3 3 0 000 6zm7.4-2.32a1.65 1.65 0 00.33 1.82l.06.06a1.998 1.998 0 01-2.83 2.83l-.06-.06a1.65 1.65 0 00-1.82-.33 1.65 1.65 0 00-1 1.51V21a2 2 0 01-4 0v-.09a1.65 1.65 0 00-1.08-1.51 1.65 1.65 0 00-1.82.33l-.06.06a1.998 1.998 0 01-2.83-2.83l.06-.06a1.65 1.65 0 00.33-1.82 1.65 1.65 0 00-1.51-1H3a2 2 0 010-4h.09a1.65 1.65 0 001.51-1.08 1.65 1.65 0 00-.33-1.82l-.06-.06a2 2 0 012.83-2.83l.06.06a1.65 1.65 0 001.82.33H9a1.65 1.65 0 001.08-1.51V3a2 2 0 014 0v.09a1.65 1.65 0 001.08 1.51 1.65 1.65 0 001.82-.33l.06-.06a2 2 0 012.83 2.83l-.06.06a1.65 1.65 0 00-.33 1.82V9c.26.01.52.08.78.08h.21a2 2 0 010 4h-.21a1.65 1.65 0 00-1.51 1.08z"),
    };

    public SidebarViewModel()
    {
        WeakReferenceMessenger.Default.Register<SidebarViewModel, TriggerStateMessage>(this,
            (vm, msg) => vm.IsDimmed = msg.Value);
    }

    /// <summary>点导航 → 发消息通知 MainWindow 切页面</summary>
    public void NavigateTo(string page)
    {
        ActivePage = page;
        WeakReferenceMessenger.Default.Send(new NavigateMessage(page));
    }
}

/// <summary>
/// 导航项：Label=显示文字，Geometry=SVG Path 数据
/// </summary>
public record NavItem(string Label, string Geometry);
