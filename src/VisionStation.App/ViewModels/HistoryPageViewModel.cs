using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionStation.Storage;
using VisionStation.Storage.Entities;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 时间范围选项（历史页用）。
///
/// 和审计页的 <c>AuditRange</c> 形状相同但**不是同一个类型**：
/// 审计问的是"谁动了参数"，历史问的是"这件为什么 NG"，
/// 两者的"今天"以后可能分道扬镳（比如历史按班次、审计按自然日），
/// 现在合并省下的 10 行，将来会在改一处时坏掉另一处。
/// </summary>
/// <param name="Name">显示名</param>
/// <param name="Days">往前几天（含今天）；null = 不限</param>
public sealed record TimeRangeOption(string Name, int? Days)
{
    /// <summary>起始时刻。用 <see cref="DateTime.Today"/>：操作员说的"今天"是 0 点起，不是过去 24 小时。</summary>
    public DateTime? Since() => Days is { } days ? DateTime.Today.AddDays(-(days - 1)) : null;

    public override string ToString() => Name;
}

/// <summary>判定筛选项；IsOk 为 null 表示"全部"</summary>
public sealed record VerdictOption(string Name, bool? IsOk)
{
    public override string ToString() => Name;
}

/// <summary>
/// 历史列表的一行。**不可变 record** —— 一条历史记录永远不会变，变的是整个列表。
/// </summary>
/// <param name="Id">记录号，用于排查时回数据库定位</param>
/// <param name="TimeText">显示用时间</param>
/// <param name="Verdict">OK / NG</param>
/// <param name="IsOk">是否合格（视图据此标色）</param>
/// <param name="EngineName">产出这条记录的引擎；换引擎前后对比就看它</param>
/// <param name="TriggerSource">Manual / Timer / PLC / VM</param>
/// <param name="ImagePath">VM 或本程序存的图；可能已被清理，选中时再判存在性</param>
/// <param name="HasImageHint">列表上标一下"有图/无图"，免得点开才发现</param>
/// <param name="PresenceDetail">定位类结论摘要</param>
/// <param name="DimensionDetail">测量类结论摘要</param>
/// <param name="DefectDetail">缺陷类结论摘要</param>
public sealed record HistoryRow(
    Guid Id,
    string TimeText,
    string Verdict,
    bool IsOk,
    string EngineName,
    string TriggerSource,
    string ImagePath,
    bool HasImageHint,
    string PresenceDetail,
    string DimensionDetail,
    string DefectDetail)
{
    public static HistoryRow From(InspectionRecord r) => new(
        r.Id,
        r.Timestamp.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        r.OverallVerdict,
        string.Equals(r.OverallVerdict, "OK", StringComparison.Ordinal),
        string.IsNullOrWhiteSpace(r.EngineName) ? "—" : r.EngineName,
        string.IsNullOrWhiteSpace(r.TriggerSource) ? "—" : r.TriggerSource,
        r.ImagePath,
        !string.IsNullOrWhiteSpace(r.ImagePath) && File.Exists(r.ImagePath),
        r.PresenceDetail,
        r.DimensionDetail,
        r.DefectDetail);

    /// <summary>
    /// 详情区的多行文字。
    ///
    /// 为什么用三组投影列而不是解析 ToolResultsJson：这三列按角色聚合，
    /// 配方里加第四个工具时它们照样是"定位/测量/缺陷"三行，不会多出看不懂的键；
    /// 而完整 JSON 是给排查用的，界面上摊开只会让操作员面对一屏乱码。
    /// </summary>
    public string DetailText
    {
        get
        {
            // 全角空格写成 \u3000 转义：直接敲出来和普通空格肉眼没区别，
            // 分析器（S2479）会当成不可见字符，review 时也看不出来。
            const string gap = "\u3000";

            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(PresenceDetail)) parts.Add($"定位{gap}{PresenceDetail}");
            if (!string.IsNullOrWhiteSpace(DimensionDetail)) parts.Add($"尺寸{gap}{DimensionDetail}");
            if (!string.IsNullOrWhiteSpace(DefectDetail)) parts.Add($"缺陷{gap}{DefectDetail}");
            return parts.Count == 0 ? "这条记录没有逐项结论" : string.Join("\n", parts);
        }
    }
}

/// <summary>
/// 检测历史页。
///
/// 为什么这一页必须存在（和审计页同一个理由，但问的是另一个问题）：
///   审计回答"昨天下午谁动过参数"，历史回答"这一件为什么判 NG、和早上的件有什么不一样"。
///   数据在 InspectionRecords 里躺了 30 天，之前**一个出口都没有**。
///   这一页走的是 <c>QueryAsync</c>/<c>CountAsync</c>（要按引擎、时间范围筛），
///   而仓储里那个不带筛选的 <c>GetRecentAsync</c> 至今只有测试在调 —— 属于可删的死代码，
///   留着不删是因为它锁了一条"取最近 N 条"的语义测试，等历史页稳定后再决定。
///   注意 <c>GetStatisticsAsync</c> **不是**零调用：今日良率在 <c>AppHost</c> 启动时就要它。
///
/// 权限用 <see cref="Core.Users.Permission.ViewHistory"/>：操作员也有。
/// 这不是"给工程师看的后台"，是操作员发现 NG 后自己要回看的那一页。
/// </summary>
public partial class HistoryPageViewModel : ObservableObject
{
    /// <summary>单次最多取多少条。再多就该缩范围，而不是往下翻几千行。</summary>
    private const int MaxRows = 300;

    /// <summary>预览图解码宽度。显示区不到 900px，按 2448 全解码是白烧 CPU 和内存。</summary>
    private const int PreviewDecodeWidth = 900;

    private const string AllEngines = "全部引擎";

    private readonly IInspectionRepository _repo;

    public ObservableCollection<HistoryRow> Rows { get; } = new();

    public IReadOnlyList<TimeRangeOption> Ranges { get; } =
    [
        new("今天", 1),
        new("近 7 天", 7),
        new("近 30 天", 30),
        new("全部", null),
    ];

    public IReadOnlyList<VerdictOption> Verdicts { get; } =
    [
        new("全部判定", null),
        new("只看 OK", true),
        new("只看 NG", false),
    ];

    /// <summary>引擎下拉。第一项固定是"全部引擎"，其余从库里动态取。</summary>
    public ObservableCollection<string> Engines { get; } = new() { AllEngines };

    [ObservableProperty]
    private TimeRangeOption? _selectedRange;

    [ObservableProperty]
    private VerdictOption? _selectedVerdict;

    [ObservableProperty]
    private string _selectedEngine = AllEngines;

    [ObservableProperty]
    private HistoryRow? _selectedRow;

    /// <summary>选中行的原图（已 Freeze，可跨线程）</summary>
    [ObservableProperty]
    private BitmapSource? _previewImage;

    /// <summary>没有图时的说明；有图时留空</summary>
    [ObservableProperty]
    private string _previewHint = "选左边一条，这里显示当时那张图";

    [ObservableProperty]
    private string _statusText = "正在加载…";

    [ObservableProperty]
    private bool _isLoading;

    public HistoryPageViewModel(IInspectionRepository repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        _repo = repo;

        // 默认今天：现场最常问的就是"刚才那件为什么 NG"。
        SelectedRange = Ranges[0];
        SelectedVerdict = Verdicts[0];
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading) return;

        IsLoading = true;
        StatusText = "正在查询…";

        try
        {
            await LoadEngineOptionsAsync();

            var query = new InspectionQuery(
                Since: SelectedRange?.Since(),
                IsOk: SelectedVerdict?.IsOk,
                EngineName: SelectedEngine == AllEngines ? null : SelectedEngine,
                Take: MaxRows);

            var rows = await _repo.QueryAsync(query);
            var total = await _repo.CountAsync(query);

            Rows.Clear();
            foreach (var record in rows)
                Rows.Add(HistoryRow.From(record));

            StatusText = rows.Count switch
            {
                0 => "这个范围内没有记录",
                _ when total > rows.Count => $"显示最近 {rows.Count} 条（共 {total} 条，缩小范围可看更早的）",
                _ => $"共 {total} 条",
            };

            // 自动选中第一条：省一次点击，而且右侧不会停在空提示上
            SelectedRow = Rows.FirstOrDefault();
        }
        catch (Exception ex)
        {
            // 查询失败要说出来：留一张空表会被读成"这段时间没有检测过"
            Rows.Clear();
            StatusText = $"查询失败：{ex.Message}";
            SelectedRow = null;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 引擎下拉从库里取。
    ///
    /// 写死 "Onnx"/"Vm" 会在换引擎那天变成错的下拉，而写死的人那天已经不在项目上了。
    /// 保留已选中的值：刷新列表不该把用户的筛选条件悄悄改回"全部"。
    /// </summary>
    private async Task LoadEngineOptionsAsync()
    {
        var names = await _repo.ListEngineNamesAsync();
        var current = SelectedEngine;

        Engines.Clear();
        Engines.Add(AllEngines);
        foreach (var name in names)
            Engines.Add(name);

        SelectedEngine = Engines.Contains(current) ? current : AllEngines;
    }

    partial void OnSelectedRowChanged(HistoryRow? value)
    {
        // 不 await：属性变更不能阻塞 UI 线程。异常在方法内部全部吃掉，转成可见的提示文字。
        _ = LoadPreviewAsync(value);
    }

    private async Task LoadPreviewAsync(HistoryRow? row)
    {
        try
        {
            if (row is null)
            {
                PreviewImage = null;
                PreviewHint = "选左边一条，这里显示当时那张图";
                return;
            }

            if (string.IsNullOrWhiteSpace(row.ImagePath))
            {
                PreviewImage = null;
                PreviewHint = "这条记录没有图像路径（VM 侧未发 imagePath）";
                return;
            }

            if (!File.Exists(row.ImagePath))
            {
                // 图和记录的生命周期不同：记录留 30 天，图可能被管家按容量先清掉
                PreviewImage = null;
                PreviewHint = $"图像已被清理或路径不可达\n{row.ImagePath}";
                return;
            }

            var path = row.ImagePath;
            var bitmap = await Task.Run(() => LoadDownscaled(path, PreviewDecodeWidth));

            PreviewImage = bitmap;
            PreviewHint = bitmap is null ? "图像无法解码（空文件或格式不支持）" : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            PreviewImage = null;
            PreviewHint = $"读图失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 按显示宽度解码，不读全分辨率。
    ///
    /// 两个必须点：① <c>CacheOption=OnLoad</c> 读完就关文件句柄 —— 否则管家删不掉这些图；
    /// ② 后台线程创建的位图必须 Freeze，否则跨线程赋给 Image.Source 会抛"归另一个线程所有"。
    /// </summary>
    private static BitmapImage? LoadDownscaled(string path, int decodeWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = decodeWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
