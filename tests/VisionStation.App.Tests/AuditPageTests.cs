using VisionStation.App.ViewModels;
using VisionStation.Core.Models;
using Xunit;

namespace VisionStation.App.Tests;

/// <summary>
/// 审计查询页。
///
/// 这一页的价值全在"能不能按条件查出想找的那条"，所以断言集中在两处：
/// 过滤条件真的传给了服务、查询失败时用户能看见。
/// </summary>
public class AuditPageTests
{
    private static AuditEntry MakeEntry(
        AuditAction action = AuditAction.Start,
        string user = "op01",
        AuditResult result = AuditResult.Success,
        string? before = null,
        string? after = null)
        => new()
        {
            Timestamp = new DateTime(2026, 10, 24, 13, 20, 0),
            UserName = user,
            UserRole = "操作员",
            Action = action,
            Target = "垫圈A",
            Before = before,
            After = after,
            Result = result,
        };

    /// <summary>默认是"今天"：复盘时最常问的就是今天</summary>
    [Fact]
    public void Constructor_DefaultsToTodayAndAllActions()
    {
        var page = new AuditPageViewModel(new FakeAuditService());

        Assert.Equal("今天", page.SelectedRange?.Name);
        Assert.Null(page.SelectedAction?.Value);
    }

    /// <summary>三个过滤条件都必须原样传给服务</summary>
    [Fact]
    public async Task Refresh_PassesFiltersToService()
    {
        var audit = new FakeAuditService();
        var page = new AuditPageViewModel(audit);

        page.SelectedRange = page.Ranges.Single(r => r.Name == "近 7 天");
        page.SelectedAction = page.Actions.Single(a => a.Value == AuditAction.EStopPressed);
        page.UserFilter = "  op01  ";   // 前后空格要被去掉

        await page.RefreshCommand.ExecuteAsync(null);

        var query = Assert.Single(audit.Queries);
        Assert.Equal(AuditAction.EStopPressed, query.Action);
        Assert.Equal("op01", query.User);
        Assert.Equal(DateTime.Today.AddDays(-6), query.Since);
        Assert.True(query.Count > 0);
    }

    /// <summary>用户留空时传 null，而不是空字符串 —— 否则会按"名字为空"过滤，一条都查不到</summary>
    [Fact]
    public async Task Refresh_WithBlankUserFilter_SendsNull()
    {
        var audit = new FakeAuditService();
        var page = new AuditPageViewModel(audit);
        page.UserFilter = "   ";

        await page.RefreshCommand.ExecuteAsync(null);

        Assert.Null(Assert.Single(audit.Queries).User);
    }

    [Fact]
    public async Task Refresh_WithRows_PopulatesTableAndCount()
    {
        var audit = new FakeAuditService();
        audit.QueryResult.Add(MakeEntry(AuditAction.Login, "admin"));
        audit.QueryResult.Add(MakeEntry(AuditAction.ModeSwitch, "op01", AuditResult.Denied));

        var page = new AuditPageViewModel(audit);
        await page.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, page.Rows.Count);
        Assert.Contains("2", page.StatusText);
    }

    [Fact]
    public async Task Refresh_WithNoRows_ExplainsItIsEmptyNotBroken()
    {
        var page = new AuditPageViewModel(new FakeAuditService());

        await page.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(page.Rows);
        Assert.Contains("没有记录", page.StatusText);
    }

    /// <summary>
    /// 查询失败必须报出来。
    ///
    /// 静默留一张空表会被误读成"这段时间没人动过" ——
    /// 而这正是审计最不该给出的错误结论。
    /// </summary>
    [Fact]
    public async Task Refresh_WhenServiceThrows_ShowsErrorAndClearsRows()
    {
        var audit = new FakeAuditService();
        audit.QueryResult.Add(MakeEntry());
        var page = new AuditPageViewModel(audit);
        await page.RefreshCommand.ExecuteAsync(null);   // 先成功一次，表里有数据
        Assert.Single(page.Rows);

        audit.QueryException = new InvalidOperationException("database is locked");
        await page.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(page.Rows);
        Assert.Contains("查询失败", page.StatusText);
        Assert.Contains("database is locked", page.StatusText);
    }

    /// <summary>变化列：有前后值才是箭头，只有后值时不要硬凑一个箭头</summary>
    [Fact]
    public void AuditRow_FormatsChangeByAvailability()
    {
        Assert.Equal("0.5 → 0.65", AuditRow.From(MakeEntry(before: "0.5", after: "0.65")).ChangeText);
        Assert.Equal("0.65", AuditRow.From(MakeEntry(after: "0.65")).ChangeText);
        Assert.Equal("—", AuditRow.From(MakeEntry()).ChangeText);
    }

    [Fact]
    public void AuditRow_MarksDeniedForHighlight()
    {
        Assert.True(AuditRow.From(MakeEntry(result: AuditResult.Denied)).IsDenied);
        Assert.False(AuditRow.From(MakeEntry(result: AuditResult.Success)).IsDenied);
        Assert.False(AuditRow.From(MakeEntry(result: AuditResult.Failed)).IsDenied);
    }

    /// <summary>动作下拉里要带中文名，不能把枚举名直接摆给操作员看</summary>
    [Fact]
    public void ActionOptions_UseChineseNames()
    {
        var page = new AuditPageViewModel(new FakeAuditService());
        var estop = page.Actions.Single(a => a.Value == AuditAction.EStopPressed);

        Assert.Equal("急停", estop.Name);
    }
}
