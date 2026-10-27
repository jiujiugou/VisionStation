using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionStation.Core.Models;
using VisionStation.Core.Users;
using VisionStation.Service.Audit;
using VisionStation.Service.Recipes;
using VisionStation.Service.Security;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 配方列表的一行 —— 从 <see cref="RecipeSummary"/> 投影出来的**只读展示快照**。
///
/// <para>
/// 为什么不直接在 XAML 里绑 <see cref="RecipeSummary"/>：那一份是存储层的契约，
/// "当前/未声明/无输出项"这些是界面措辞。把它们留在 VM 里，
/// 将来改文案不必动 Service 层，也不必在 XAML 里堆一堆转换器。
/// </para>
/// <para>
/// <b>归属</b>：每次刷新整表重建，不可变 —— 磁盘上的配方换了就是新的一行。
/// </para>
/// </summary>
/// <param name="Name">配方自报的名字（可能为空 → 显示"（未命名）"）</param>
/// <param name="FileName">磁盘文件名，切换时唯一标识</param>
/// <param name="IsActive">是否是 <c>.active</c> 指针指向的那一份</param>
/// <param name="Engine">配方声明的引擎名，如实展示不过滤</param>
/// <param name="OutputCount">声明的输出项数</param>
/// <param name="Width">画面宽，0 = 未声明</param>
/// <param name="Height">画面高，0 = 未声明</param>
/// <param name="UpdatedAt">最近一次经本程序保存的时间；null = 没存过</param>
public sealed record RecipeRow(
    string Name,
    string FileName,
    bool IsActive,
    string Engine,
    int OutputCount,
    int Width,
    int Height,
    DateTimeOffset? UpdatedAt)
{
    /// <summary>把存储层快照投影成一行。空名字给一个占位，不要留一块空白让人以为没加载出来。</summary>
    public static RecipeRow From(RecipeSummary summary) => new(
        string.IsNullOrWhiteSpace(summary.Name) ? "（未命名）" : summary.Name,
        summary.FileName,
        summary.IsActive,
        summary.Engine,
        summary.OutputCount,
        summary.Width,
        summary.Height,
        summary.UpdatedAt);

    /// <summary>引擎列。空串在表里看起来像"这一列还没做完"，破折号明确表示"文件没写"。</summary>
    public string EngineText => string.IsNullOrWhiteSpace(Engine) ? "—" : Engine;

    /// <summary>画面尺寸。0×0 是"文件没声明"，不是"画面是 0 像素"。</summary>
    public string SizeText => Width > 0 && Height > 0
        ? $"{Width}×{Height}"
        : "未声明";

    /// <summary>输出项数。0 项意味着这份配方激活后不会有任何结果项，切换前值得看见。</summary>
    public string OutputText => OutputCount > 0 ? $"{OutputCount} 项" : "无输出项";

    /// <summary>保存时间（只到分钟：配方是换型用的，秒级精度没人看）。</summary>
    public string UpdatedText => UpdatedAt is { } time
        ? time.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
        : "—";
}

/// <summary>
/// 「配方切换」页 —— 换型时的唯一入口（F-48）。
///
/// <para>
/// <b>这一页做什么</b>：列出配方目录里所有能解析的配方，把选中的那份写成
/// <c>.active</c> 指针，并留一条 <see cref="AuditAction.RecipeSwitch"/> 流水。
/// </para>
///
/// <para>
/// <b>这一页不做什么（关键，别在这里加"已生效"文案）</b>：
/// 运行时配方是组合根在启动时加载的 DI 单例 <c>RecipeConfig</c>，
/// 引擎/工作流/各面板都吃那一个实例，而且<b>没有热重载通道</b>
/// （原 <c>IInspectionEngine.Reload()</c> 已删除）。所以这里切的只是
/// "下次启动加载哪一份" —— 本进程仍跑着启动时那份，**必须重启才换过去**。
/// 页面上那句常驻提示就是为此存在的；任何"已重载/已生效"的字样都是假成功。
/// </para>
///
/// <para>
/// <b>权限</b>：<see cref="Permission.SwitchRecipe"/>（技术员及以上）。
/// 侧边栏隐藏入口只是减少误触，不是防线 —— 命令里再校验一次，
/// 被拒时记 <see cref="AuditResult.Denied"/>：这条记录比十次成功更值得看。
/// </para>
///
/// <para>
/// <b>失败行为：不允许"看起来切成功了其实没切"</b>。三条防线——
/// ① 文件不存在由 <see cref="IRecipeStore.SetActiveAsync"/> 抛；
/// ② 写指针后<b>读回校验</b> <see cref="IRecipeStore.ActiveName"/>
///    （存储层把指针写失败记成警告不抛，不读回就发现不了）；
/// ③ 失败一律转成页面提示 + <see cref="AuditResult.Failed"/>。
/// </para>
///
/// <para>
/// <b>归属</b>：DI 单例（组合根决定），构造只存依赖、<b>不读盘</b>；
/// 首次加载由 View 的 <c>Loaded</c> 触发 <see cref="RefreshCommand"/>。
/// </para>
/// </summary>
public partial class RecipePageViewModel : ObservableObject
{
    /// <summary>配方存储。本页只用 ListAsync / SetActiveAsync / ActiveName，不保存配方内容。</summary>
    private readonly IRecipeStore _store;

    /// <summary>会话。切换前按有效角色再校验一次 <see cref="Permission.SwitchRecipe"/>。</summary>
    private readonly ISessionService _session;

    /// <summary>审计。切换成功/被拒/失败都要留痕，三态各有含义（见 <see cref="AuditResult"/>）。</summary>
    private readonly IAuditService _audit;

    /// <summary>当前列表。整表替换，不做增量 —— 磁盘变了就是重新读过。</summary>
    public ObservableCollection<RecipeRow> Rows { get; } = new();

    /// <summary>配方目录（绝对路径）。显示出来是为了让人能自己去核对/备份。</summary>
    public string Folder => _store.Folder;

    /// <summary>选中行。切换命令的作用对象。</summary>
    [ObservableProperty]
    private RecipeRow? _selectedRow;

    /// <summary>页面提示行：列表状态、切换结果、失败原因都写这里。</summary>
    [ObservableProperty]
    private string _statusText = "正在加载配方列表…";

    /// <summary>提示行是否为警告语气。**不要用它报成功**。</summary>
    [ObservableProperty]
    private bool _statusIsWarning;

    /// <summary>读盘/写指针进行中。用来挡重复点击，同时把按钮置灰。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// 按钮可用性。XAML 里没有取反转换器，所以由 VM 直接给出正向属性，
    /// 免得为了一个 <c>!</c> 再造一个转换器。
    /// </summary>
    public bool IsIdle => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    /// <summary>
    /// 构造：只保存依赖，**不读盘、不查会话**（AGENTS.md 第 7 条）。
    /// 列表加载由页面首次显示触发 —— 那才是"用户真的要这一页"的时刻。
    /// </summary>
    public RecipePageViewModel(IRecipeStore store, ISessionService session, IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(audit);

        _store = store;
        _session = session;
        _audit = audit;
    }

    /// <summary>
    /// 重新从磁盘读一遍列表（手动「刷新」与切换成功后都走它）。
    ///
    /// 切换成功后必须重读而不是本地改 <c>IsActive</c>：列表是磁盘的影子，
    /// 本地改一份就会出现"界面显示已切换、指针其实还在原处"那种最坏情况。
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        try
        {
            await ReloadRowsAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 把选中那份设为激活配方。
    ///
    /// <para>
    /// 校验顺序是刻意的：**先判有没有选、再判权限、最后才碰磁盘**。
    /// 权限判断放在写盘之前，被拒时磁盘上不会留下任何改动。
    /// </para>
    /// <para>
    /// 已经是激活项时直接返回、不记流水：审计是流水不是点击计数，
    /// 重复条目会把"真换过几次型"淹掉。
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task SwitchAsync()
    {
        if (IsBusy) return;

        var row = SelectedRow;
        if (row is null)
        {
            ShowWarning("请先在列表里选一份配方。");
            return;
        }

        // 侧边栏只是隐藏入口，不能当唯一防线：命令里按有效角色再判一次。
        // 被拒要留痕（Denied）—— "有人尝试越权"比十次正常切换更值得看。
        if (!_session.HasPermission(Permission.SwitchRecipe))
        {
            _audit.Record(AuditAction.RecipeSwitch, row.FileName,
                result: AuditResult.Denied,
                detail: "无切换配方权限（需要技术员及以上）");

            ShowWarning($"当前账号没有切换配方的权限（需要技术员及以上）。\n激活配方未改动，仍是「{SafeActiveName()}」。");
            return;
        }

        if (row.IsActive)
        {
            ShowInfo($"「{row.FileName}」已经是激活配方，本次未做改动。");
            return;
        }

        IsBusy = true;
        try
        {
            // 切换前的指针值：审计要能回答"从哪份换到哪份"。
            var before = SafeActiveName();

            try
            {
                await _store.SetActiveAsync(row.FileName);
            }
            catch (FileNotFoundException)
            {
                // 列表是上一次读盘的结果，文件可能刚被人在资源管理器里删掉。
                _audit.Record(AuditAction.RecipeSwitch, row.FileName,
                    before: before, after: null,
                    result: AuditResult.Failed,
                    detail: "配方文件不存在");

                ShowWarning($"切换失败：配方文件已不存在 —— {row.FileName}\n"
                            + $"激活配方未改动，仍是「{before}」。点「刷新」重新读取目录。");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _audit.Record(AuditAction.RecipeSwitch, row.FileName,
                    before: before, after: null,
                    result: AuditResult.Failed,
                    detail: ex.Message);

                ShowWarning($"切换失败：{ex.Message}\n激活配方未改动，仍是「{before}」。");
                return;
            }

            // 读回校验：存储层把"指针写失败"记成警告不抛（对保存流程是合理的降级），
            // 但对本页就是"看起来切了其实没切"。只有读回一致才算成功。
            var actual = SafeActiveName();
            if (!string.Equals(actual, row.FileName, StringComparison.OrdinalIgnoreCase))
            {
                _audit.Record(AuditAction.RecipeSwitch, row.FileName,
                    before: before, after: actual,
                    result: AuditResult.Failed,
                    detail: $"写激活指针后读回的是「{actual}」");

                ShowWarning($"切换没有生效：写激活指针失败，磁盘上仍然是「{actual}」。\n"
                            + "配方目录可能只读或被其他程序占用，处理后点「刷新」重试。");
                return;
            }

            _audit.Record(AuditAction.RecipeSwitch, row.FileName,
                before: before, after: actual,
                detail: "激活指针已更新；下次启动生效");

            // 列表状态回到磁盘：整表重读，不本地改 IsActive。
            await ReloadRowsAsync(keepSelection: row.FileName);

            // 文案必须说清"什么时候生效" —— 这里没有热重载，说"已切换"会被读成"现在就在用新配方"。
            ShowInfo($"已把「{row.FileName}」设为激活配方，下次启动生效。\n"
                     + "本次运行仍在用启动时加载的配方，重启程序后才会换过去。");
        }
        catch (Exception ex)
        {
            // 兜底：SetActiveAsync 之外的意外（如目录不可访问）也要变成人话，
            // 不能让异常从 async void 命令里冒到 UI 线程。
            _audit.Record(AuditAction.RecipeSwitch, row.FileName,
                result: AuditResult.Failed, detail: ex.Message);

            ShowWarning($"切换失败：{ex.Message}\n激活配方未改动。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 读目录并重建列表。
    ///
    /// <para>
    /// <paramref name="keepSelection"/> 为 null 时保留当前选中项（按文件名找回），
    /// 找不到就退到激活项、再退到第一行 —— 刷新不该让用户重新找一遍刚才选的那份。
    /// </para>
    /// <para>
    /// 失败时**清空列表并写原因**：留一行旧数据会被读成"目录还是那样"，
    /// 而实际情况可能是目录根本读不到。
    /// </para>
    /// </summary>
    private async Task ReloadRowsAsync(string? keepSelection = null)
    {
        var selection = keepSelection ?? SelectedRow?.FileName;

        try
        {
            var summaries = await _store.ListAsync();

            Rows.Clear();
            foreach (var summary in summaries)
                Rows.Add(RecipeRow.From(summary));

            SelectedRow =
                Rows.FirstOrDefault(r => string.Equals(r.FileName, selection, StringComparison.OrdinalIgnoreCase))
                ?? Rows.FirstOrDefault(r => r.IsActive)
                ?? Rows.FirstOrDefault();

            StatusText = Rows.Count == 0
                ? $"配方目录里没有可用的配方：{Folder}\n（只认声明了 engine 的 json；坏文件会被跳过）"
                : $"共 {Rows.Count} 份配方 · {Folder}";
            StatusIsWarning = false;
        }
        catch (Exception ex)
        {
            Rows.Clear();
            SelectedRow = null;
            StatusText = $"读取配方目录失败：{ex.Message}\n目录：{Folder}";
            StatusIsWarning = true;
        }
    }

    /// <summary>
    /// 读当前激活文件名，读不到返回占位文字。
    ///
    /// 只用于提示文案与审计的 before —— 指针读失败不该让"切换"这个动作本身炸掉，
    /// 真正的判定在切换后的读回校验里。
    /// </summary>
    private string SafeActiveName()
    {
        try
        {
            var name = _store.ActiveName;
            return string.IsNullOrWhiteSpace(name) ? "（无）" : name;
        }
        catch (Exception)
        {
            return "（读不到）";
        }
    }

    /// <summary>提示行：普通说明。**不要用它报失败**。</summary>
    private void ShowInfo(string text)
    {
        StatusText = text;
        StatusIsWarning = false;
    }

    /// <summary>提示行：警告语气，用于权限不足、切换失败、目录读不到。</summary>
    private void ShowWarning(string text)
    {
        StatusText = text;
        StatusIsWarning = true;
    }
}
