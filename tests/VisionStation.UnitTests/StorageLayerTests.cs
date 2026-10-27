using System.Globalization;
using Microsoft.Data.Sqlite;
using VisionStation.Core.Models;
using VisionStation.Storage;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// Storage 切片（F-S02..F-S04）的防线用例。
/// 每条对应 RequirementCatalog 里的一条约束，命令写在约束的 command 字段里。
///
/// <para>
/// 原本这里还有 R-S01 / R-S05 / R-S06 三条（存图路径写回、文件名唯一、抽样率校验），
/// 2026-10-27 随 <c>ImageStoreService</c> 一起删除：相机层删掉后本程序不再写图，
/// 图由 VisionMaster 存盘、本程序只按报文路径读，那个服务已经没有生产调用者。
/// 留下的三条是 <see cref="InspectionRepository"/> 与 <see cref="StorageSchema"/> 的，
/// 它们仍在生产路径上。
/// </para>
/// </summary>
public class StorageLayerTests
{
    private static string NewRoot()
    {
        var d = Path.Combine(Path.GetTempPath(), "vs_st_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static InspectionReport MakeReport(string trigger = "Test")
    {
        var r = new InspectionReport { TriggerSource = trigger, Engine = "TestEngine" };
        // 键是业务名、Role 决定进哪一列 —— 与真实引擎的产出方式一致
        r.SetToolResult("有无", new ToolResult { IsOk = true, Summary = "有", Role = ResultKeys.Locate });
        r.SetToolResult("尺寸", new ToolResult { IsOk = true, Summary = "尺寸合格", Role = ResultKeys.Measure });
        r.SetToolResult("缺陷", new ToolResult { IsOk = true, Summary = "无缺陷", Role = ResultKeys.Defect });
        return r;
    }

    private static List<string> ReadColumns(string dbPath, string table = "InspectionRecords")
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var rd = cmd.ExecuteReader();
        var cols = new List<string>();
        while (rd.Read()) cols.Add(rd.GetString(1));
        return cols;
    }

    private static void ExecSql(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ── R-S02：全部工具结果都要落库 ────────────────────────────────
    [Fact]
    public async Task R_S02_AllToolResultsPersisted()
    {
        var dbPath = Path.Combine(NewRoot(), "s02.db");
        var repo = new InspectionRepository(dbPath);
        await repo.EnsureCreatedAsync();

        var report = MakeReport();
        report.SetToolResult("划痕", ToolResult.Ng("孔位缺失"));   // 第四项（无角色，只进 JSON 快照）

        await repo.SaveAsync(report);

        var rows = await repo.QueryAsync(new InspectionQuery { Take = 1 });
        Assert.Single(rows);
        Assert.Equal("NG", rows[0].OverallVerdict);

        using var doc = System.Text.Json.JsonDocument.Parse(rows[0].ToolResultsJson);
        var props = doc.RootElement.EnumerateObject().ToList();
        Assert.Equal(4, props.Count);
        Assert.False(doc.RootElement.GetProperty("划痕").GetProperty("IsOk").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("有无").GetProperty("IsOk").GetBoolean());
    }

    // ── R-S03：老库缺列要能自动补，且不丢数据 ──────────────────────
    [Fact]
    public async Task R_S03_OldSchemaGetsUpgraded()
    {
        var dbPath = Path.Combine(NewRoot(), "s03.db");

        var v1 = new InspectionRepository(dbPath);
        await v1.EnsureCreatedAsync();
        await v1.SaveAsync(MakeReport("补列前"));

        // 用 DROP COLUMN 模拟「上一版代码建的库」：结构里没有 ToolResultsJson
        ExecSql(dbPath, "ALTER TABLE \"InspectionRecords\" DROP COLUMN \"ToolResultsJson\";");
        Assert.DoesNotContain("ToolResultsJson", ReadColumns(dbPath));

        var v2 = new InspectionRepository(dbPath);
        await v2.EnsureCreatedAsync();      // 这里必须自动补列

        Assert.Contains("ToolResultsJson", ReadColumns(dbPath));

        var rows = await v2.QueryAsync(new InspectionQuery { Take = 10 });
        Assert.Single(rows);                              // 旧数据没被清掉
        Assert.Equal("补列前", rows[0].TriggerSource);
        // 补列默认值是空串，不是 "{}"：旧行本来就没有这份快照，
        // 写成 "{}" 会让人误以为「当时确实一个工具都没跑」。空串如实表示「这一版之前没有这个字段」。
        Assert.Equal("", rows[0].ToolResultsJson);

        // 补列之后能正常写入
        await v2.SaveAsync(MakeReport("补列后"));
        Assert.Equal(2, (await v2.QueryAsync(new InspectionQuery { Take = 10 })).Count);
    }

    // ── R-S03（审计表）：补列不能只认检测记录表 ──────────────────────
    [Fact]
    public async Task R_S03_OldAuditTableGetsUpgraded()
    {
        var dbPath = Path.Combine(NewRoot(), "s03_audit.db");

        var v1 = new AuditRepository(dbPath);
        await v1.EnsureCreatedAsync();
        await v1.SaveAsync(new AuditEntry
        {
            UserName = "op01",
            UserRole = "Operator",
            Action = AuditAction.ParameterChange,
            Detail = "补列前",
        });

        // 用 DROP COLUMN 模拟「上一版代码建的库」：结构里没有 Detail。
        // Detail 没有索引、不是主键，满足 SQLite DROP COLUMN 的限制；
        // 这条语句与 R-S03 用的是同一个手法（本机 SQLite 版本已支持）。
        ExecSql(dbPath, "ALTER TABLE \"AuditLogs\" DROP COLUMN \"Detail\";");
        Assert.DoesNotContain("Detail", ReadColumns(dbPath, "AuditLogs"));

        var v2 = new AuditRepository(dbPath);
        await v2.EnsureCreatedAsync();          // 这里必须给审计表补列，而不是只补检测表

        Assert.Contains("Detail", ReadColumns(dbPath, "AuditLogs"));

        var rows = await v2.QueryRecentAsync();
        Assert.Single(rows);                    // 旧数据没被清掉
        Assert.Equal("op01", rows[0].UserName);
        // Detail 在模型里是 string?，补列必须是可空列；老行拿到 NULL，与新建库一致
        Assert.Null(rows[0].Detail);

        // 补列之后能正常写入。这里故意不给 Detail 赋值（仓储会写 null）：
        // 若补列时把它加成 NOT NULL DEFAULT ''，这一句会抛 NOT NULL constraint failed ——
        // 「列补上了但审计仍然写不进去」同样是上线才会发现的缺陷。
        await v2.SaveAsync(new AuditEntry
        {
            UserName = "op02",
            UserRole = "Operator",
            Action = AuditAction.Login,
        });
        await v2.SaveAsync(new AuditEntry
        {
            UserName = "op03",
            UserRole = "Operator",
            Action = AuditAction.Login,
            Detail = "补列后",
        });

        var after = await v2.QueryRecentAsync();
        Assert.Equal(3, after.Count);
        Assert.Equal("补列后", after[0].Detail);   // 按 Id 倒序，最新一条在最前
        Assert.Null(after[1].Detail);              // 可空列可以正常写 null
    }

    // ── R-S04：并发写不因锁等待失败（WAL + 5s 上限）────────────────
    [Fact]
    public async Task R_S04_ConcurrentWrites_NoBusyFailure()
    {
        var dbPath = Path.Combine(NewRoot(), "s04.db");
        var repo = new InspectionRepository(dbPath);
        await repo.EnsureCreatedAsync();

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode";
            var mode = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            Assert.Equal("wal", mode?.ToLowerInvariant());
        }

        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            for (var k = 0; k < 20; k++)
                await repo.SaveAsync(MakeReport($"t{i}-{k}"));
        }));

        var ex = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
        Assert.Null(ex);

        var (ok, ng) = await repo.GetStatisticsAsync();
        Assert.Equal(160, ok + ng);
        Assert.Equal(0, ng);
    }
}
