using System.IO;
using VisionStation.App.ViewModels;
using VisionStation.Core.Models;
using VisionStation.Core.Users;
using VisionStation.Service.Recipes;

namespace VisionStation.App.Tests;

/// <summary>
/// 配方切换页（F-48）的行为契约。
///
/// <para>
/// 这一册测的不是"列表长得对不对"，而是四件会让人在产线上做错决定的事：
/// <list type="number">
///   <item>切完**指针真的写下去了**（读回校验，不是看返回值）；</item>
///   <item>成功时**留下一条 RecipeSwitch 流水**，且 before/after 是真文件名；</item>
///   <item>无权限时**一个字节都没写**（侧边栏隐藏入口不是防线）；</item>
///   <item>失败时**不许出现"已生效"字样** —— 存储层把写指针失败吞成警告，
///         不读回就会出现"看着切好了、重启还是旧配方"。</item>
/// </list>
/// </para>
/// </summary>
public class RecipePageViewModelTests
{
    /// <summary>
    /// 列表：目录里有几份就出几行，激活项自动选中，摘要字段照文件如实展示。
    ///
    /// 自动选中激活项不是"贴心"，而是让操作员一进页面就看见"现在用的是哪份"，
    /// 不必在列表里找哪个带"当前"标记。
    /// </summary>
    [Fact]
    public async Task Refresh_LoadsAllRecipesAndSelectsActiveOne()
    {
        var store = new FakeRecipeStore { ActiveName = "b.json" };
        store.Summaries.Add(Summary("垫圈 A", "a.json", engine: "Vm", outputs: 3));
        store.Summaries.Add(Summary("垫圈 B", "b.json", engine: "Vm", outputs: 1));

        var vm = new RecipePageViewModel(store, SignedIn(UserRole.Technician), new FakeAuditService());

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("b.json", vm.SelectedRow!.FileName);
        Assert.True(vm.Rows.Single(r => r.FileName == "b.json").IsActive);
        Assert.False(vm.Rows.Single(r => r.FileName == "a.json").IsActive);

        var row = vm.Rows.Single(r => r.FileName == "a.json");
        Assert.Equal("垫圈 A", row.Name);
        Assert.Equal("Vm", row.EngineText);
        Assert.Equal("3 项", row.OutputText);
        Assert.Equal("1280×1024", row.SizeText);
        Assert.False(vm.StatusIsWarning);
    }

    /// <summary>
    /// 没声明引擎 / 没配输出项的配方要显示成人能看懂的样子。
    ///
    /// 0 项输出不是"还没加载"，是这份配方激活后不会有任何结果项 ——
    /// 界面上必须说"无输出项"，而不是留一个 0 让人以为统计还没算。
    /// </summary>
    [Fact]
    public async Task Refresh_RendersMissingDeclarationsAsWords()
    {
        var store = new FakeRecipeStore { ActiveName = "bare.json" };
        store.Summaries.Add(Summary("裸配方", "bare.json", engine: "", outputs: 0, width: 0, height: 0));

        var vm = new RecipePageViewModel(store, SignedIn(UserRole.Technician), new FakeAuditService());

        await vm.RefreshCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Rows);
        Assert.Equal("—", row.EngineText);
        Assert.Equal("无输出项", row.OutputText);
        Assert.Equal("未声明", row.SizeText);
        Assert.Equal("—", row.UpdatedText);
    }

    /// <summary>
    /// 目录读不到：转成人话提示 + 清空列表，不把异常抛给 UI 线程。
    ///
    /// 留着上一次的列表比报错更危险 —— 那会被读成"目录还是那些配方"。
    /// </summary>
    [Fact]
    public async Task Refresh_WhenDirectoryUnreadable_ShowsWarningAndClearsRows()
    {
        var store = new FakeRecipeStore { ListError = new UnauthorizedAccessException("拒绝访问") };
        var vm = new RecipePageViewModel(store, SignedIn(UserRole.Technician), new FakeAuditService());

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(vm.Rows);
        Assert.Null(vm.SelectedRow);
        Assert.True(vm.StatusIsWarning);
        Assert.Contains("读取配方目录失败", vm.StatusText);
    }

    /// <summary>
    /// 切换成功：写指针一次、记一条 <see cref="AuditAction.RecipeSwitch"/>、
    /// 列表回到磁盘状态、并且**明说重启后生效**。
    ///
    /// before/after 必须是文件名而不是"切换成功"：事后复盘要回答的是
    /// "这一班到底从哪份换到了哪份"。
    /// </summary>
    [Fact]
    public async Task Switch_WithPermission_WritesPointerAndAudits()
    {
        var store = new FakeRecipeStore { ActiveName = "a.json" };
        store.Summaries.Add(Summary("垫圈 A", "a.json"));
        store.Summaries.Add(Summary("垫圈 B", "b.json"));

        var audit = new FakeAuditService();
        var vm = new RecipePageViewModel(store, SignedIn(UserRole.Technician), audit);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedRow = vm.Rows.Single(r => r.FileName == "b.json");

        await vm.SwitchCommand.ExecuteAsync(null);

        Assert.Equal("b.json", Assert.Single(store.SetActiveCalls));
        Assert.Equal("b.json", store.ActiveName);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditAction.RecipeSwitch, entry.Action);
        Assert.Equal("b.json", entry.Target);
        Assert.Equal("a.json", entry.Before);
        Assert.Equal("b.json", entry.After);
        Assert.Equal(AuditResult.Success, entry.Result);

        Assert.True(vm.Rows.Single(r => r.FileName == "b.json").IsActive);
        Assert.False(vm.Rows.Single(r => r.FileName == "a.json").IsActive);
        Assert.False(vm.StatusIsWarning);
        Assert.Contains("重启", vm.StatusText);
        // 没有热重载：不许出现"已生效/已重载"这类假成功文案
        Assert.DoesNotContain("已生效", vm.StatusText);
        Assert.DoesNotContain("已重载", vm.StatusText);
    }

    /// <summary>
    /// 无权限：拒绝、**一个字节都不写**，并留一条 Denied 流水。
    ///
    /// 为什么 denied 也要记：权限位被拒说明有人在试。只记成功等于
    /// 把最该看的那段历史删掉（见 <see cref="AuditResult"/> 的三态说明）。
    /// </summary>
    [Fact]
    public async Task Switch_WithoutPermission_DoesNotWriteAndRecordsDenied()
    {
        var store = new FakeRecipeStore { ActiveName = "a.json" };
        store.Summaries.Add(Summary("垫圈 A", "a.json"));
        store.Summaries.Add(Summary("垫圈 B", "b.json"));

        var audit = new FakeAuditService();
        // 未登录 → HasPermission(SwitchRecipe) 为 false（操作员也不够，切配方是技术员的权限）
        var vm = new RecipePageViewModel(store, new FakeSessionService(), audit);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedRow = vm.Rows.Single(r => r.FileName == "b.json");

        await vm.SwitchCommand.ExecuteAsync(null);

        Assert.Empty(store.SetActiveCalls);
        Assert.Equal("a.json", store.ActiveName);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditAction.RecipeSwitch, entry.Action);
        Assert.Equal(AuditResult.Denied, entry.Result);

        Assert.True(vm.StatusIsWarning);
        Assert.Contains("权限", vm.StatusText);
        Assert.False(vm.Rows.Single(r => r.FileName == "b.json").IsActive);
    }

    /// <summary>
    /// **写指针被吞掉**：SetActiveAsync 正常返回，但读回来的激活项还是旧的。
    ///
    /// 这是本页最该防的一种失败 —— <c>JsonRecipeStore.WritePointer</c> 写不进去只记警告、
    /// 不抛异常（对保存流程是合理降级）。不读回校验，界面就会显示"已切换"，
    /// 而重启后装的还是旧配方，现场到那时才发现判据全错。
    /// </summary>
    [Fact]
    public async Task Switch_WhenPointerWriteIsLost_ReportsFailureAndKeepsActiveUnchanged()
    {
        var store = new FakeRecipeStore { ActiveName = "a.json", SwallowPointerWrite = true };
        store.Summaries.Add(Summary("垫圈 A", "a.json"));
        store.Summaries.Add(Summary("垫圈 B", "b.json"));

        var audit = new FakeAuditService();
        var vm = new RecipePageViewModel(store, SignedIn(UserRole.Engineer), audit);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedRow = vm.Rows.Single(r => r.FileName == "b.json");

        await vm.SwitchCommand.ExecuteAsync(null);

        Assert.Equal("b.json", Assert.Single(store.SetActiveCalls));
        Assert.Equal("a.json", store.ActiveName);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditResult.Failed, entry.Result);

        Assert.True(vm.StatusIsWarning);
        Assert.Contains("没有生效", vm.StatusText);
        // 列表必须仍是磁盘上的事实：a.json 是激活项
        Assert.True(vm.Rows.Single(r => r.FileName == "a.json").IsActive);
        Assert.False(vm.Rows.Single(r => r.FileName == "b.json").IsActive);
        // 任何"成功了"的措辞都不许出现
        Assert.DoesNotContain("已把", vm.StatusText);
    }

    /// <summary>
    /// 文件在列表读完之后被人删了：存储层抛 FileNotFoundException，
    /// 页面要说"文件已不存在"，并明确激活配方没动。
    /// </summary>
    [Fact]
    public async Task Switch_WhenFileVanished_ReportsFailureWithoutChangingActive()
    {
        var store = new FakeRecipeStore
        {
            ActiveName = "a.json",
            SetActiveError = new FileNotFoundException("配方不存在：b.json"),
        };
        store.Summaries.Add(Summary("垫圈 A", "a.json"));
        store.Summaries.Add(Summary("垫圈 B", "b.json"));

        var audit = new FakeAuditService();
        var vm = new RecipePageViewModel(store, SignedIn(UserRole.Technician), audit);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedRow = vm.Rows.Single(r => r.FileName == "b.json");

        await vm.SwitchCommand.ExecuteAsync(null);

        Assert.Equal("a.json", store.ActiveName);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditResult.Failed, entry.Result);

        Assert.True(vm.StatusIsWarning);
        Assert.Contains("不存在", vm.StatusText);

        // 失败后列表没被重读成"B 已激活"
        Assert.True(vm.Rows.Single(r => r.FileName == "a.json").IsActive);
    }

    /// <summary>
    /// 选中的就是激活项：不写盘、不记流水，只说明"本次未做改动"。
    ///
    /// 审计是流水不是点击计数 —— 重复条目会把"真换过几次型"淹掉。
    /// </summary>
    [Fact]
    public async Task Switch_WhenAlreadyActive_DoesNothing()
    {
        var store = new FakeRecipeStore { ActiveName = "a.json" };
        store.Summaries.Add(Summary("垫圈 A", "a.json"));

        var audit = new FakeAuditService();
        var vm = new RecipePageViewModel(store, SignedIn(UserRole.Technician), audit);

        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.SwitchCommand.ExecuteAsync(null);

        Assert.Empty(store.SetActiveCalls);
        Assert.Empty(audit.Entries);
        Assert.False(vm.StatusIsWarning);
        Assert.Contains("已经是激活配方", vm.StatusText);
    }

    /// <summary>
    /// 没选任何行（目录为空）时点切换：只说"先选一份"，不去碰磁盘。
    /// 这里最容易写出的错误是 NullReferenceException —— 那会让按钮表现成"没反应"。
    /// </summary>
    [Fact]
    public async Task Switch_WithoutSelection_WarnsAndDoesNotTouchStore()
    {
        var store = new FakeRecipeStore { ActiveName = null };
        var audit = new FakeAuditService();
        var vm = new RecipePageViewModel(store, SignedIn(UserRole.Technician), audit);

        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.SwitchCommand.ExecuteAsync(null);

        Assert.Empty(store.SetActiveCalls);
        Assert.Empty(audit.Entries);
        Assert.True(vm.StatusIsWarning);
        Assert.Contains("先", vm.StatusText);
    }

    // ══════════════════ 替身与构造助手 ══════════════════

    /// <summary>
    /// 可完全操控的配方存储替身。
    ///
    /// 为什么不复用 <c>TestDoubles.NoopRecipeStore</c>：那个只够"能构造出来"，
    /// 不记调用、不能模拟"指针写失败但方法正常返回"——而那正是本页最关键的失败模式。
    /// </summary>
    private sealed class FakeRecipeStore : IRecipeStore
    {
        public string Folder => @"C:\fake\recipes";

        /// <summary>可写：切换成功时更新它，读回校验读的也是它。</summary>
        public string? ActiveName { get; set; }

        public List<RecipeSummary> Summaries { get; } = new();

        /// <summary>SetActiveAsync 的入参序列（用来断言"一次、且是这一份"）。</summary>
        public List<string> SetActiveCalls { get; } = new();

        /// <summary>非空时 SetActiveAsync 抛它（文件消失、目录只读这类）。</summary>
        public Exception? SetActiveError { get; set; }

        /// <summary>
        /// true = 方法正常返回但**指针没动** —— 复刻 <c>JsonRecipeStore.WritePointer</c>
        /// 把 IO 异常吞成警告的行为，用来验读回校验。
        /// </summary>
        public bool SwallowPointerWrite { get; set; }

        /// <summary>非空时 ListAsync 抛它（目录不可访问）。</summary>
        public Exception? ListError { get; set; }

        public Task<string> SaveAsync(RecipeConfig recipe, CancellationToken ct = default)
            => throw new NotSupportedException("配方页不保存配方内容");

        public Task<IReadOnlyList<RecipeSummary>> ListAsync(CancellationToken ct = default)
        {
            if (ListError is not null) throw ListError;

            // 与真实实现同口径：IsActive 由当前指针推出，而不是文件里写的那个值
            IReadOnlyList<RecipeSummary> rows = Summaries
                .Select(s => s with
                {
                    IsActive = string.Equals(s.FileName, ActiveName, StringComparison.OrdinalIgnoreCase),
                })
                .ToList();

            return Task.FromResult(rows);
        }

        public Task<RecipeConfig?> LoadAsync(string fileName, CancellationToken ct = default)
            => Task.FromResult<RecipeConfig?>(null);

        public Task SetActiveAsync(string fileName, CancellationToken ct = default)
        {
            SetActiveCalls.Add(fileName);

            if (SetActiveError is not null) throw SetActiveError;
            if (!SwallowPointerWrite) ActiveName = fileName;

            return Task.CompletedTask;
        }
    }

    /// <summary>已经登录成某角色的会话（真实权限矩阵走 <see cref="RolePermissions"/>）。</summary>
    private static FakeSessionService SignedIn(UserRole role)
    {
        var session = new FakeSessionService();
        session.SignIn(role);
        return session;
    }

    /// <summary>
    /// 造一份列表快照。尺寸固定 1280×1024，除非用例要验"未声明"。
    /// <paramref name="updatedAt"/> 默认 null = "从没经本程序保存过"（显示" — "）。
    /// </summary>
    private static RecipeSummary Summary(
        string name,
        string fileName,
        string engine = "Vm",
        int outputs = 2,
        int width = 1280,
        int height = 1024,
        DateTimeOffset? updatedAt = null)
        => new(name, fileName, width, height, updatedAt, engine, IsActive: false)
        {
            OutputCount = outputs,
        };
}
