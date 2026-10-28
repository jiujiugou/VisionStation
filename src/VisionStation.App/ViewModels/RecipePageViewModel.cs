using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionStation.App.Abstractions;
using VisionStation.App.Services.Recipes;
using VisionStation.App.Services.Restart;
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

    /// <summary>
    /// 方案目录门面（F-67/F-68）。导入区读名册、切换前跑契约校验，都走它——
    /// 路径解析（assetRoot）收在门面里，本类只见文件名和问题清单。
    /// </summary>
    private readonly ISolutionCatalog _solutions;

    /// <summary>一键重启（F-69）。只表达"重启"，真拉起后继进程在 App.OnExit。</summary>
    private readonly IAppRestarter _restarter;

    /// <summary>重启前的确认框。重启会结束本次运行，必须问一句。</summary>
    private readonly IDialogService _dialog;

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

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanRestartNow));
    }

    /// <summary>
    /// 构造：只保存依赖，**不读盘、不查会话**（AGENTS.md 第 7 条）。
    /// 列表加载由页面首次显示触发 —— 那才是"用户真的要这一页"的时刻。
    /// </summary>
    public RecipePageViewModel(
        IRecipeStore store,
        ISessionService session,
        IAuditService audit,
        ISolutionCatalog solutions,
        IAppRestarter restarter,
        IDialogService dialog)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(solutions);
        ArgumentNullException.ThrowIfNull(restarter);
        ArgumentNullException.ThrowIfNull(dialog);

        _store = store;
        _session = session;
        _audit = audit;
        _solutions = solutions;
        _restarter = restarter;
        _dialog = dialog;
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
            // F-68 契约闸（2026-10-28）：目标配方按名字引用的模块必须真的在它声明的方案里。
            // 列表显示"能解析"，解析通过 ≠ 配对正确——空壳方案案（JSON 指着没有检测模块的 .sol）
            // 就是这道闸要抓的。契约不通过绝不写激活指针。
            var target = await _store.LoadAsync(row.FileName);
            if (target is null)
            {
                _audit.Record(AuditAction.RecipeSwitch, row.FileName,
                    before: SafeActiveName(), after: null,
                    result: AuditResult.Failed,
                    detail: "切换前读取配方失败");

                ShowWarning($"切换失败：配方读取失败 —— {row.FileName}\n"
                            + $"激活配方未改动，仍是「{SafeActiveName()}」。点「刷新」重试。");
                return;
            }

            var contract = _solutions.CheckContract(target);
            if (contract.Count > 0)
            {
                _audit.Record(AuditAction.RecipeSwitch, row.FileName,
                    before: SafeActiveName(), after: null,
                    result: AuditResult.Failed,
                    detail: "契约校验未通过：" + string.Join("；", contract));

                ShowWarning("切换被拦：这份配方与它声明的方案对不上 ——\n"
                            + string.Join("\n", contract)
                            + $"\n激活配方未改动，仍是「{SafeActiveName()}」。");
                return;
            }

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
            // 「重启生效」按钮（F-69）随之亮起：把"手动关闭再打开"变一次点击。
            RequiresRestart = true;
            ShowInfo($"已把「{row.FileName}」设为激活配方，下次启动生效。\n"
                     + "本次运行仍在用启动时加载的配方。点「重启生效」立即换过去，或稍后自行重启。");
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

            // 方案清单（F-67 导入区）。ListSolutionFiles 内部自带兜底（目录不存在/读不了都回空清单），
            // 不会把配方列表一起带崩——两边是不同的目录，互不连坐。
            AvailableSolutions.Clear();
            foreach (var solution in _solutions.ListSolutionFiles())
                AvailableSolutions.Add(solution);

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

    // ══════════════════════════════════════════════════════════════════
    // 从方案导入（F-67，2026-10-28）
    //
    // 目的：把"手写 JSON"变成"从 .sol 解析 + 三个业务决定"。
    // 机器能给的：模块名册（SolutionManifestReader，明文可解析）。
    // 机器不能给的：订阅顺序（在 VM 通信配置的二进制里）、语义角色、人话 label——
    //   这三个决定全部留给操作者，界面上改，改完保存。
    // 边界：**编辑不落盘，保存必落盘，运行时只认 JSON** —— .sol 是"编辑时的源"，
    //   JSON 才是"运行时的真相"，两个真相源是配对断裂案的根因，不再犯。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>solutions 目录里的 .sol 文件名清单（页面加载时随配方列表一起刷新）。</summary>
    public ObservableCollection<string> AvailableSolutions { get; } = new();

    /// <summary>选中要导入的方案文件名。</summary>
    [ObservableProperty]
    private string? _selectedSolution;

    /// <summary>导入预览行。集合的顺序 = 报文段顺序，排序靠上移/下移命令。</summary>
    public ObservableCollection<ImportOutputRow> ImportRows { get; } = new();

    /// <summary>新配方的名字（同时决定文件名）。</summary>
    [ObservableProperty]
    private string _importName = "";

    /// <summary>画面宽（文本框直填；空或非法按"未声明"处理，与既有配方口径一致）。</summary>
    [ObservableProperty]
    private string _importWidthText = "";

    /// <summary>画面高。</summary>
    [ObservableProperty]
    private string _importHeightText = "";

    /// <summary>导入区自己的状态行。与页面主状态分开：导入失败不该把列表状态冲掉。</summary>
    [ObservableProperty]
    private string _importStatusText = "选择方案文件后点「解析名册」。";

    /// <summary>导入区状态是否为警告语气。**不要用它报成功**。</summary>
    [ObservableProperty]
    private bool _importStatusIsWarning;

    /// <summary>
    /// 切换成功后点亮「重启生效」按钮（F-69）。进页面时不亮：没换过配方就重启是无意义的动作。
    /// </summary>
    [ObservableProperty]
    private bool _requiresRestart;

    /// <summary>「重启生效」按钮可用性：换过配方、且当前没有读写在进行。</summary>
    public bool CanRestartNow => RequiresRestart && !IsBusy;

    /// <summary>
    /// 解析选中的方案，生成导入预览。
    ///
    /// <para>预填规则与 tools/RecipeGen v1 完全一致（同一套规则的两处实现，
    /// 改一处必须改另一处）：剔除基础设施、存图模块摘出、逻辑汇总提前。</para>
    /// </summary>
    [RelayCommand]
    private void ParseSolution()
    {
        if (IsBusy) return;

        var solution = SelectedSolution;
        if (string.IsNullOrWhiteSpace(solution))
        {
            ImportWarn("请先选择一个方案文件（.sol）。");
            return;
        }

        try
        {
            var manifest = _solutions.ReadManifest(solution);
            var candidates = manifest.DraftCandidates(out var saveImage);

            if (candidates.Count == 0)
            {
                ImportRows.Clear();
                ImportWarn($"方案「{manifest.SolutionName}」的名册里没有可作输出的模块"
                           + "（只有图像源/脚本等基础设施）——这份方案可能没有检测流程。");
                return;
            }

            ImportRows.Clear();
            // Select 而不是在循环里 new：S3267 会拦（也确实更好读）。
            foreach (var row in candidates.Select(module => new ImportOutputRow
                     {
                         ModuleName = module.DisplayName,
                         Label = module.DisplayName,
                         Role = "Defect",
                         // metric 预填 = label：解析器按它建 Metrics 键，漏了它历史页按名查数值就是空的
                         //（v1 CLI 漏过这两个字段，界面版补上——见 2026-10-28 字段审计）。
                         Metric = module.DisplayName,
                         Unit = "",
                     }))
            {
                ImportRows.Add(row);
            }

            if (string.IsNullOrWhiteSpace(ImportName))
                ImportName = manifest.SolutionName;

            var hint = $"方案「{manifest.SolutionName}」：名册 {manifest.Modules.Count} 个模块，"
                       + $"候选 {candidates.Count} 项已预填（顺序按名册序，逻辑汇总已提前）。"
                       + "顺序必须对照 VisionMaster「发送数据」订阅核对后再启用。";
            if (saveImage is not null)
                hint += $" 另：名册里有存图模块（{saveImage.DisplayName}）——若订阅了存图路径，可手工在末尾追加。";
            ImportInfo(hint);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ImportRows.Clear();
            ImportWarn($"方案解析失败：{ex.Message}");
        }
    }

    /// <summary>把一行上移一位（报文段顺序提前）。集合顺序即段顺序。</summary>
    [RelayCommand]
    private void MoveUp(ImportOutputRow? row)
    {
        if (row is null) return;
        var index = ImportRows.IndexOf(row);
        if (index <= 0) return;
        ImportRows.Move(index, index - 1);
    }

    /// <summary>把一行下移一位。</summary>
    [RelayCommand]
    private void MoveDown(ImportOutputRow? row)
    {
        if (row is null) return;
        var index = ImportRows.IndexOf(row);
        if (index < 0 || index >= ImportRows.Count - 1) return;
        ImportRows.Move(index, index + 1);
    }

    /// <summary>
    /// 把预览落成一份新配方 JSON。
    ///
    /// <para>防线顺序：忙碌 → 权限（先于一切写盘）→ 输入齐全 → label 非空且不重 →
    /// 尺寸可解析 → <see cref="RecipeConfig.Validate"/> → 契约校验（草稿按名册生成，
    /// 理应通过；过不了说明实现有 bug，拦下来比写出一个坏配方强）→ 落盘。</para>
    /// </summary>
    [RelayCommand]
    private async Task SaveImportAsync()
    {
        if (IsBusy) return;

        // 导入是配置写入，与切换同级：SwitchRecipe（技术员及以上），先于一切磁盘动作复核。
        if (!_session.HasPermission(Permission.SwitchRecipe))
        {
            _audit.Record(AuditAction.RecipeImport, SelectedSolution ?? "（未选方案）",
                result: AuditResult.Denied,
                detail: "无导入配方权限（需要技术员及以上）");
            ImportWarn("当前账号没有导入配方的权限（需要技术员及以上）。");
            return;
        }

        var solution = SelectedSolution;
        if (string.IsNullOrWhiteSpace(solution))
        {
            ImportWarn("还没有选择方案文件。");
            return;
        }
        if (ImportRows.Count == 0)
        {
            ImportWarn("还没有解析出输出项——先点「解析名册」。");
            return;
        }

        var selected = ImportRows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ImportWarn("至少保留一个勾选的输出项（一项都不选的配方检测不出任何东西）。");
            return;
        }

        var name = ImportName.Trim();
        if (name.Length == 0)
        {
            ImportWarn("请填写配方名称——它是现场唯一能用来认配方的东西。");
            return;
        }

        var duplicated = selected.GroupBy(r => r.Label.Trim(), StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).FirstOrDefault();
        if (duplicated is not null)
        {
            ImportWarn($"label 重复：{duplicated}。同名会互相覆盖，改成不同的名字（如 螺丝1、螺丝2）。");
            return;
        }
        if (selected.Any(r => r.Label.Trim().Length == 0))
        {
            ImportWarn("有输出项的 label 是空的——label 是结果键，不能留空。");
            return;
        }

        if (!TryParseSize(ImportWidthText, out var width) || !TryParseSize(ImportHeightText, out var height))
        {
            ImportWarn("画面尺寸格式不对：填正整数（像素），留空表示未声明。");
            return;
        }

        var fileName = SanitizeFileName(name);
        if (fileName.Length == 0)
        {
            ImportWarn("配方名称里没有可用于文件名的字符，换一个名字。");
            return;
        }

        IsBusy = true;
        try
        {
            var manifest = _solutions.ReadManifest(solution);
            var config = new RecipeConfig
            {
                Name = name,
                Engine = EngineNames.Vm,
                Note = BuildImportNote(manifest, selected),
                Sensor = { Width = width, Height = height },
                Vm = { SolutionPath = "Config\\solutions\\" + solution },
            };
            foreach (var row in selected)
            {
                config.Vm.Outputs.Add(new VmOutputMapping
                {
                    Module = row.ModuleName,
                    Label = row.Label.Trim(),
                    Role = row.Role,
                    Metric = string.IsNullOrWhiteSpace(row.Metric) ? row.Label.Trim() : row.Metric.Trim(),
                    Unit = row.Unit?.Trim() ?? "",
                });
            }

            var problems = config.Validate();
            if (problems.Count > 0)
            {
                ImportWarn("配方没有通过校验：\n" + string.Join("\n", problems));
                return;
            }

            // 防御性复核（F-68 同一道闸）：草稿按名册生成理应通过；
            // 过不了说明预填逻辑有 bug——拦下来，不写出一个契约断裂的配方。
            var contract = _solutions.CheckContract(config);
            if (contract.Count > 0)
            {
                ImportWarn("导入被契约校验拦下（这不该发生，请把这条信息报给工程师）：\n"
                           + string.Join("\n", contract));
                return;
            }

            string savedPath;
            try
            {
                savedPath = await _store.SaveNewAsync(fileName, config);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _audit.Record(AuditAction.RecipeImport, fileName,
                    result: AuditResult.Failed, detail: ex.Message);
                ImportWarn($"保存失败：{ex.Message}");
                return;
            }

            _audit.Record(AuditAction.RecipeImport, fileName,
                detail: $"从方案 {solution} 导入 {selected.Count} 项输出（{Path.GetFileName(savedPath)}）");

            await ReloadRowsAsync(keepSelection: fileName);

            // 新配方**未激活**（SaveNewAsync 不写指针）——诚实交代启用路径，别让人以为导完就在用。
            ImportInfo($"已保存为「{fileName}」（未激活）。要启用：在列表选中它 → 「切换为激活配方」→ 重启。");
        }
        catch (Exception ex)
        {
            _audit.Record(AuditAction.RecipeImport, fileName,
                result: AuditResult.Failed, detail: ex.Message);
            ImportWarn($"导入失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 一键重启（F-69）：走正常关闭路径结束本进程，App.OnExit 收尾完成后拉起后继实例。
    /// 这里**绝不**直接 Kill / 直接 Process.Start —— 端口还没释放、容器还没释放。
    /// </summary>
    [RelayCommand]
    private async Task RestartNowAsync()
    {
        if (!CanRestartNow) return;

        // 重启的本质是"让新配方上场"，权限与切换同级；侧边栏隐藏不算防线，这里再校验一次。
        if (!_session.HasPermission(Permission.SwitchRecipe))
        {
            _audit.Record(AuditAction.RecipeSwitch, SafeActiveName(),
                result: AuditResult.Denied,
                detail: "请求重启生效新配方，但无切换配方权限");
            ShowWarning("当前账号没有重启程序的权限（需要技术员及以上）。");
            return;
        }

        var confirmed = await _dialog.ConfirmAsync(
            "重启程序",
            $"重启后程序将加载激活配方「{SafeActiveName()}」。\n"
            + "请确认 VisionMaster 里已加载对应的方案文件。现在重启吗？");
        if (!confirmed) return;

        _audit.Record(AuditAction.RecipeSwitch, SafeActiveName(),
            detail: "一键重启以加载新配方（F-69）：走正常关闭，收尾后由 OnExit 拉起后继实例");

        // 先标记意图、再走正常关闭；真拉起在 OnExit（端口与容器都释放之后）。
        _restarter.Restart();
    }

    /// <summary>尺寸文本 → 像素。空 = 0（未声明，与既有配方口径一致）；其余必须是正整数。</summary>
    private static bool TryParseSize(string? text, out int value)
    {
        var trimmed = text?.Trim();
        if (trimmed is null || trimmed.Length == 0)
        {
            value = 0;
            return true;
        }
        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    /// <summary>配方名 → 安全文件名。剔除非法字符；剔完为空由调用方提示。</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return clean.Length == 0 ? "" : clean + ".json";
    }

    /// <summary>
    /// 导入配方的 note：报文段顺序的唯一记载（订阅顺序机器拿不到，见 <see cref="SolutionManifest"/>）。
    /// 生成器不装懂——顺序必须人工核对这句话原样写进去。
    /// </summary>
    private static string BuildImportNote(SolutionManifest manifest, IReadOnlyList<ImportOutputRow> selected)
    {
        var order = string.Join("，", selected.Select((r, i) => $"第{i + 1}段={r.Label.Trim()}"));
        return $"报文段顺序（分隔符 ;）：{order}。注意：顺序以本文件为准，"
               + "改 VisionMaster「发送数据」订阅后必须同步改这里。"
               + $"（本配方由配方页从方案 {manifest.SolutionName} 导入生成，2026-10-28）";
    }

    private void ImportInfo(string text)
    {
        ImportStatusText = text;
        ImportStatusIsWarning = false;
    }

    private void ImportWarn(string text)
    {
        ImportStatusText = text;
        ImportStatusIsWarning = true;
    }
}

/// <summary>
/// 导入预览的一行。集合顺序 = 报文段顺序（用 <c>ObservableCollection.Move</c> 维护），
/// 所以这里没有 Order 字段——多一个可编辑的序号就会出现"序号和实际顺序打架"。
/// </summary>
public partial class ImportOutputRow : ObservableObject
{
    /// <summary>方案里的模块显示名（契约面，与名册核对的就是它；只读——改名等于改契约）。</summary>
    public required string ModuleName { get; init; }

    /// <summary>是否纳入 outputs。取消勾选 = 这一段不进配方（预览里保留行，方便勾回来）。</summary>
    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>结果业务名（结果键，必须唯一）。</summary>
    [ObservableProperty]
    private string _label = "";

    /// <summary>结果角色：Locate / Measure / Defect（见 <see cref="VmOutputMapping.Role"/>）。</summary>
    [ObservableProperty]
    private string _role = "Defect";

    /// <summary>Metrics 字典的键名。预填 = label；被存储层当列名用，起错了旧列就是 0。</summary>
    [ObservableProperty]
    private string _metric = "";

    /// <summary>物理单位（如 mm），仅摘要显示。</summary>
    [ObservableProperty]
    private string _unit = "";
}
