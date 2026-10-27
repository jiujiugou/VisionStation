using VisionStation.App.ViewModels;
using VisionStation.Storage;
using VisionStation.Storage.Entities;

namespace VisionStation.App.Tests;

/// <summary>
/// 历史页把筛选条件交给仓储的那一段（F-32 条码检索的界面侧）。
///
/// <para>
/// 为什么这一层值得单独测：筛选本身（SQL）在 <c>InspectionQueryTests</c> 里，
/// 但"用户在输入框里打的字有没有真的变成查询条件"只有这里能验。
/// 这一层错了不会报错 —— 界面照样刷新，只是筛了个寂寞，
/// 而人会以为"这个条码确实没有记录"。
/// </para>
/// </summary>
public class HistoryPageViewModelTests
{
    /// <summary>造一条够历史页显示用的记录（无图，避免 VM 去解码）。</summary>
    private static InspectionRecord Record(string sn, string verdict = "OK")
        => new()
        {
            Timestamp = new DateTime(2026, 10, 27, 9, 0, 0, DateTimeKind.Local),
            OverallVerdict = verdict,
            EngineName = "VisionMaster",
            TriggerSource = "VM",
            Sn = sn,
            ImagePath = "",
        };

    [Fact]
    public async Task 条码原样交给仓储_裁剪由仓储统一做()
    {
        var repo = new RecordingInspectionRepository();
        var vm = new HistoryPageViewModel(repo) { SnFilter = "  A001 " };

        await vm.RefreshCommand.ExecuteAsync(null);

        // VM 不自己 Trim：裁剪只在仓储一处做，否则"界面裁一遍、仓储再裁一遍"，
        // 迟早有一处漏掉，而那只会表现为"偶发查不到"。
        Assert.NotNull(repo.LastQuery);
        Assert.Equal("  A001 ", repo.LastQuery!.Sn);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 条码留空_不把空串当查询条件(string input)
    {
        var repo = new RecordingInspectionRepository();
        var vm = new HistoryPageViewModel(repo) { SnFilter = input };

        await vm.RefreshCommand.ExecuteAsync(null);

        // 传空串下去在 SQL 里就是 Sn == ''，会只返回"没扫码"的那些帧 ——
        // 看起来像"查询坏了"，实际是条件被当成了真条件。所以这里必须是 null。
        Assert.Null(repo.LastQuery!.Sn);
    }

    [Fact]
    public async Task 列表与计数用同一套条件_条码不会只作用于一边()
    {
        var repo = new RecordingInspectionRepository();
        var vm = new HistoryPageViewModel(repo) { SnFilter = "A001" };

        await vm.RefreshCommand.ExecuteAsync(null);

        // 只给列表加条件、忘了给计数加，界面就会显示"0 条 / 共 1 条"。
        Assert.Equal(repo.LastQuery!.Sn, repo.LastCountQuery!.Sn);
        Assert.Equal("A001", repo.LastQuery.Sn);
    }

    [Fact]
    public async Task 有条码的记录_列表行原样显示条码()
    {
        var repo = new RecordingInspectionRepository();
        repo.Rows.Add(Record("A001-2026"));

        var vm = new HistoryPageViewModel(repo);
        await vm.RefreshCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Rows);
        Assert.Equal("A001-2026", row.Sn);
    }

    [Fact]
    public void 没条码的记录_显示破折号而不是空白()
    {
        // 空着看起来像"这一列还没做完"；破折号明确表示"这一帧就是没有"。
        Assert.Equal("—", HistoryRow.From(Record("")).Sn);
        Assert.Equal("—", HistoryRow.From(Record("   ")).Sn);
    }

    [Fact]
    public async Task 条码查不到_状态文字要说明是条码没命中()
    {
        // 空列表 + 一句"这个范围内没有记录"，会被读成"这段时间没检测过"——
        // 而实际是条码没对上（输错、或那一帧本来就没带 SN）。两种结论差得很远。
        var repo = new RecordingInspectionRepository();
        var vm = new HistoryPageViewModel(repo) { SnFilter = "A001" };

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(vm.Rows);
        Assert.Contains("A001", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("条码", vm.StatusText, StringComparison.Ordinal);
    }
}
