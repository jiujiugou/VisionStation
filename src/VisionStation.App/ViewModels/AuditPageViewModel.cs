using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionStation.Core.Models;
using VisionStation.Service.Audit;

namespace VisionStation.App.ViewModels;

/// <summary>审计动作的中文名。界面与导出共用一份，避免两处各写一套对不上。</summary>
internal static class AuditActionText
{
    public static string Of(AuditAction action) => action switch
    {
        AuditAction.AppStart => "程序启动",
        AuditAction.AppStop => "程序退出",
        AuditAction.Start => "启动检测",
        AuditAction.Stop => "停止检测",
        AuditAction.EStopPressed => "急停",
        AuditAction.EStopReset => "复位急停",
        AuditAction.ModeSwitch => "模式切换",
        AuditAction.EngineReload => "重载引擎",
        AuditAction.RecipeSwitch => "切换配方",
        AuditAction.ParameterChange => "修改参数",
        AuditAction.Login => "登录",
        AuditAction.Logout => "登出",
        AuditAction.LoginFailed => "登录失败",
        AuditAction.LoginLocked => "登录锁定",
        AuditAction.SessionExpired => "会话超时",
        AuditAction.UserCreate => "新建用户",
        AuditAction.UserDisable => "停用用户",
        AuditAction.PasswordChange => "修改密码",
        // 新增枚举值忘了配文案时退回英文名，而不是显示空白
        _ => action.ToString(),
    };

    public static string ResultOf(AuditResult result) => result switch
    {
        AuditResult.Success => "成功",
        AuditResult.Denied => "拒绝",
        AuditResult.Failed => "失败",
        _ => result.ToString(),
    };
}

/// <summary>
/// 时间范围选项。
/// </summary>
/// <param name="Name">显示名</param>
/// <param name="Days">往前几天（含今天）；null = 不限</param>
public sealed record AuditRange(string Name, int? Days)
{
    /// <summary>
    /// 起始时刻。用 <see cref="DateTime.Today"/> 而不是"当前时间减 N 天" ——
    /// 操作员说"今天"指的是 0 点起，不是过去 24 小时。
    /// </summary>
    public DateTime? Since() => Days is { } days ? DateTime.Today.AddDays(-(days - 1)) : null;

    public override string ToString() => Name;
}

/// <summary>动作下拉项；Value 为 null 表示"全部"</summary>
public sealed record AuditActionOption(string Name, AuditAction? Value)
{
    public override string ToString() => Name;
}

/// <summary>
/// 表格里的一行。审计记录是只读历史，不需要 ObservableObject ——
/// 用不可变 record，一行的内容永远不会变，变的是整个列表。
/// </summary>
/// <param name="IsDenied">是否为"被拒绝"，视图据此标红</param>
public sealed record AuditRow(
    string TimeText,
    string UserName,
    string UserRole,
    string ActionText,
    string Target,
    string ChangeText,
    string ResultText,
    bool IsDenied,
    string Detail)
{
    public static AuditRow From(AuditEntry e) => new(
        e.Timestamp.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        e.UserName,
        e.UserRole,
        AuditActionText.Of(e.Action),
        string.IsNullOrEmpty(e.Target) ? "—" : e.Target,
        BuildChange(e.Before, e.After),
        AuditActionText.ResultOf(e.Result),
        e.Result == AuditResult.Denied,
        e.Detail ?? string.Empty);

    /// <summary>
    /// 变化列："0.5 → 0.65"。
    ///
    /// 只记 after 的情况（比如登录，没有"改前值"）就只显示 after ——
    /// 硬凑一个箭头会让人以为是修改记录。
    /// </summary>
    private static string BuildChange(string? before, string? after)
    {
        var hasBefore = !string.IsNullOrEmpty(before);
        var hasAfter = !string.IsNullOrEmpty(after);

        if (hasBefore && hasAfter) return $"{before} → {after}";
        if (hasAfter) return after!;
        if (hasBefore) return before!;
        return "—";
    }
}

/// <summary>
/// 审计查询页。
///
/// 为什么这一页必须存在：审计数据躺在 SQLite 里但没有出口，
/// 等于"记了但没人能看"。出了质量事故要回答"昨天下午谁动过参数"时，
/// 让人去敲 sqlite3 是不现实的。
///
/// 只给管理员开（<see cref="Core.Users.Permission.ViewAudit"/>）：
/// 工程师能改参数，但不能查审计 —— 否则等于让他确认自己的脚印有没有被记下来。
/// </summary>
public partial class AuditPageViewModel : ObservableObject
{
    /// <summary>单次最多取多少条。再多就该缩范围，而不是往下翻几千行</summary>
    private const int MaxRows = 500;

    private readonly IAuditService _audit;

    public ObservableCollection<AuditRow> Rows { get; } = new();

    public IReadOnlyList<AuditRange> Ranges { get; } =
    [
        new("今天", 1),
        new("近 7 天", 7),
        new("近 30 天", 30),
        new("全部", null),
    ];

    public IReadOnlyList<AuditActionOption> Actions { get; }

    [ObservableProperty]
    private AuditRange? _selectedRange;

    [ObservableProperty]
    private AuditActionOption? _selectedAction;

    /// <summary>用户名过滤（空 = 全部）。用文本框而不是下拉：账号是随时会增的</summary>
    [ObservableProperty]
    private string _userFilter = string.Empty;

    /// <summary>底部统计/空态文案</summary>
    [ObservableProperty]
    private string _statusText = "正在加载…";

    [ObservableProperty]
    private bool _isLoading;

    public AuditPageViewModel(IAuditService audit)
    {
        _audit = audit;

        SelectedRange = Ranges[0];   // 默认今天：复盘最常问的就是"今天/刚才"
        Actions = BuildActions();
        SelectedAction = Actions[0];
    }

    private static IReadOnlyList<AuditActionOption> BuildActions()
    {
        var options = new List<AuditActionOption> { new("全部动作", null) };

        // 按枚举定义顺序，而不是字母序：这个顺序本身是有意义的
        // （程序生命周期 → 运行操作 → 会话 → 账号管理）
        foreach (var action in Enum.GetValues<AuditAction>())
            options.Add(new AuditActionOption(AuditActionText.Of(action), action));

        return options;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading) return;

        IsLoading = true;
        StatusText = "正在查询…";

        try
        {
            var rows = await _audit.QueryAsync(
                SelectedAction?.Value,
                string.IsNullOrWhiteSpace(UserFilter) ? null : UserFilter.Trim(),
                SelectedRange?.Since(),
                MaxRows);

            Rows.Clear();
            foreach (var entry in rows)
                Rows.Add(AuditRow.From(entry));

            StatusText = rows.Count switch
            {
                0 => "这个范围内没有记录",
                >= MaxRows => $"已显示最近 {MaxRows} 条（还有更多，请缩小时间范围）",
                _ => $"共 {rows.Count} 条",
            };
        }
        catch (Exception ex)
        {
            // 查询失败要说出来：静默留一张空表，会被误读成"这段时间没有操作"
            Rows.Clear();
            StatusText = $"查询失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
