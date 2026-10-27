using VisionStation.Core.Models;
using VisionStation.Storage;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// 历史查询（<see cref="IInspectionRepository.QueryAsync"/> 等）的防线用例。
///
/// 为什么值得测：历史页是"出了问题之后唯一能查的地方"。
/// 它筛错了不会报错，只会安静地少显示几条 —— 有人查不到昨天的 NG，
/// 就会得出"昨天没出问题"的结论，这比查不到更糟。
/// </summary>
public class InspectionQueryTests : IDisposable
{
    /// <summary>引擎下拉的期望值。提成静态字段：CA1861 会把每次调用都新建的数组参数记一条。</summary>
    private static readonly string[] ExpectedEngines = ["EngineA", "EngineB"];

    private readonly string _root;
    private readonly string _dbPath;

    public InspectionQueryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vs_q_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "test.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* 文件被占用时留给系统清理，不影响断言结果 */ }

        // 显式抑制终结：这个类没有终结器，不写的话分析器会报 CA1816，
        // 而棘轮是按总条数卡门禁的 —— 测试代码的告警同样占额度。
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 造一条可入库的报告。
    /// 总判定由结果项推导（一项不合格整件不合格），所以这里用 IsOk 控制唯一的那一项。
    /// </summary>
    private static InspectionReport Report(bool isOk, string engine, DateTime timestamp,
        string trigger = "Test", string sn = "")
    {
        var report = new InspectionReport
        {
            Engine = engine,
            TriggerSource = trigger,
            Timestamp = timestamp,
            Sn = sn,
        };
        report.SetToolResult("外径", new ToolResult
        {
            IsOk = isOk,
            Summary = isOk ? "12.00mm 合格" : "12.60mm 超差",
            Role = ResultKeys.Measure,
        });
        return report;
    }

    /// <summary>
    /// 建一个装了 5 条记录的库：
    /// 3 条 OK（引擎 A）、2 条 NG（引擎 B），时间从 10:00 到 10:04。
    /// 其中第 1 条带条码 <c>A001</c>、第 3 条带 <c>A002</c>，其余不带（真实工位常见：多数帧没有扫码）。
    /// </summary>
    private InspectionRepository SeededRepository()
    {
        var repo = new InspectionRepository(_dbPath);
        repo.EnsureCreatedAsync().GetAwaiter().GetResult();

        var day = new DateTime(2026, 10, 24, 10, 0, 0, DateTimeKind.Local);

        repo.SaveAsync(Report(true, "EngineA", day.AddMinutes(0), sn: "A001")).GetAwaiter().GetResult();
        repo.SaveAsync(Report(true, "EngineA", day.AddMinutes(1))).GetAwaiter().GetResult();
        repo.SaveAsync(Report(false, "EngineB", day.AddMinutes(2), sn: "A002")).GetAwaiter().GetResult();
        repo.SaveAsync(Report(true, "EngineA", day.AddMinutes(3))).GetAwaiter().GetResult();
        repo.SaveAsync(Report(false, "EngineB", day.AddMinutes(4))).GetAwaiter().GetResult();

        return repo;
    }

    [Fact]
    public async Task 不筛选时返回全部_且按时间倒序()
    {
        var repo = SeededRepository();

        var rows = await repo.QueryAsync(new InspectionQuery());

        Assert.Equal(5, rows.Count);
        Assert.True(rows[0].Timestamp > rows[^1].Timestamp, "历史必须最新的在最上面");
    }

    [Fact]
    public async Task 只看NG_不混进OK()
    {
        var repo = SeededRepository();

        var rows = await repo.QueryAsync(new InspectionQuery(IsOk: false));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("NG", r.OverallVerdict));
    }

    [Fact]
    public async Task 只看OK_不混进NG()
    {
        var repo = SeededRepository();

        var rows = await repo.QueryAsync(new InspectionQuery(IsOk: true));

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("OK", r.OverallVerdict));
    }

    [Fact]
    public async Task 按引擎筛_只留该引擎的记录()
    {
        var repo = SeededRepository();

        var rows = await repo.QueryAsync(new InspectionQuery(EngineName: "EngineB"));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("EngineB", r.EngineName));
    }

    [Fact]
    public async Task 时间范围是左闭右开_不重复不漏()
    {
        var repo = SeededRepository();
        var day = new DateTime(2026, 10, 24, 10, 0, 0, DateTimeKind.Local);

        // [10:01, 10:03) —— 期望正好 2 条：10:01 与 10:02
        var rows = await repo.QueryAsync(new InspectionQuery(
            Since: day.AddMinutes(1),
            Until: day.AddMinutes(3)));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.InRange(r.Timestamp, day.AddMinutes(1), day.AddMinutes(2)));
    }

    [Fact]
    public async Task 多个条件是且的关系_不是或()
    {
        var repo = SeededRepository();

        // EngineA 有 3 条但全是 OK，加"只看 NG"之后应该是 0 条。
        // 如果实现成了"或"，这里会返回 5 条 —— 那种错误最容易被当成"查询没生效"。
        var rows = await repo.QueryAsync(new InspectionQuery(IsOk: false, EngineName: "EngineA"));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Take限制返回条数_但计数仍是全部()
    {
        var repo = SeededRepository();
        var query = new InspectionQuery(Take: 2);

        var rows = await repo.QueryAsync(query);
        var total = await repo.CountAsync(query);

        Assert.Equal(2, rows.Count);
        Assert.Equal(5, total);   // 界面要能说出"共 5 条，显示最近 2 条"
    }

    [Fact]
    public async Task 计数与列表用同一套筛选_不会出现条数对不上()
    {
        var repo = SeededRepository();
        var query = new InspectionQuery(IsOk: true, EngineName: "EngineA");

        var rows = await repo.QueryAsync(query);
        var total = await repo.CountAsync(query);

        Assert.Equal(rows.Count, total);
    }

    [Fact]
    public async Task 引擎下拉_去重且不含空值()
    {
        var repo = SeededRepository();

        var engines = await repo.ListEngineNamesAsync();

        Assert.Equal(ExpectedEngines, engines);
    }

    // ── 按产品条码检索（F-32）─────────────────────────────────────
    // 现场用法：客户拿着实物报条码，或操作员在历史页扫一下要回看那一件。
    // 查错不会报错，只会安静地返回空列表 —— 有人会因此得出"这件没检过"。

    [Fact]
    public async Task 按条码查_只返回那一条()
    {
        var repo = SeededRepository();

        var rows = await repo.QueryAsync(new InspectionQuery(Sn: "A001"));

        var row = Assert.Single(rows);
        Assert.Equal("A001", row.Sn);
        // 条码定位到的是"那一件"，所以它的判定/时间必须是那一件的，不能串行
        Assert.Equal("OK", row.OverallVerdict);
    }

    [Fact]
    public async Task 条码查询忽略首尾空白()
    {
        // 界面输入框里粘条码常带空格，扫码枪也可能带一个；库里的值是写入时裁过的。
        // 两边不裁就会永久对不上，而现象只是"输入了却查不到"。
        var repo = SeededRepository();

        var rows = await repo.QueryAsync(new InspectionQuery(Sn: "  A002 \t"));

        Assert.Equal("A002", Assert.Single(rows).Sn);
    }

    [Fact]
    public async Task 条码是精确匹配_不是前缀或子串()
    {
        // 条码是"这一件"的唯一标识，输一半就命中三条会让人不知道该看哪条。
        // 要按前缀找时，由调用方自己拼 —— 别在这一层放宽语义。
        var repo = SeededRepository();

        Assert.Empty(await repo.QueryAsync(new InspectionQuery(Sn: "A00")));
        Assert.Empty(await repo.QueryAsync(new InspectionQuery(Sn: "A0011")));
    }

    [Fact]
    public async Task 条码留空_等于不筛选()
    {
        var repo = SeededRepository();

        Assert.Equal(5, (await repo.QueryAsync(new InspectionQuery(Sn: null))).Count);
        Assert.Equal(5, (await repo.QueryAsync(new InspectionQuery(Sn: ""))).Count);
        Assert.Equal(5, (await repo.QueryAsync(new InspectionQuery(Sn: "   "))).Count);
    }

    [Fact]
    public async Task 条码与判定是且的关系()
    {
        var repo = SeededRepository();

        // A002 那条是 NG；再加"只看 OK"后必须一条不剩。
        // 若实现成"或"，这里会返回 3 条（全部 OK）+ 1 条（A002），一眼看不出错。
        Assert.Empty(await repo.QueryAsync(new InspectionQuery(Sn: "A002", IsOk: true)));
        Assert.Single(await repo.QueryAsync(new InspectionQuery(Sn: "A002", IsOk: false)));
    }

    [Fact]
    public async Task 条码筛选也作用于计数_不会出现列表空但共N条()
    {
        var repo = SeededRepository();

        Assert.Equal(1, await repo.CountAsync(new InspectionQuery(Sn: "A001")));
        Assert.Equal(0, await repo.CountAsync(new InspectionQuery(Sn: "不存在")));
    }
}
