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
    private static InspectionReport Report(bool isOk, string engine, DateTime timestamp, string trigger = "Test")
    {
        var report = new InspectionReport
        {
            Engine = engine,
            TriggerSource = trigger,
            Timestamp = timestamp,
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
    /// </summary>
    private InspectionRepository SeededRepository()
    {
        var repo = new InspectionRepository(_dbPath);
        repo.EnsureCreatedAsync().GetAwaiter().GetResult();

        var day = new DateTime(2026, 10, 24, 10, 0, 0, DateTimeKind.Local);

        repo.SaveAsync(Report(true, "EngineA", day.AddMinutes(0))).GetAwaiter().GetResult();
        repo.SaveAsync(Report(true, "EngineA", day.AddMinutes(1))).GetAwaiter().GetResult();
        repo.SaveAsync(Report(false, "EngineB", day.AddMinutes(2))).GetAwaiter().GetResult();
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
}
