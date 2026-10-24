using Microsoft.Data.Sqlite;
using VisionStation.Core.Models;
using VisionStation.Service.Audit;
using VisionStation.Storage;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// 审计链路的防线用例。
///
/// 重点不在"能存能查"（任何 CRUD 都能过），而在三个真会出问题的地方：
///   1) 现场老库补表 —— EnsureCreated 不给已有库建新表，这条不测就会在现场炸；
///   2) 写失败不反噬 —— 审计是旁路，它挂了检测必须继续跑；
///   3) 三态结果 —— Denied 是审计里最该看的一条，存不进去等于没记。
/// </summary>
public class AuditTests
{
    private static string NewDbPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs_audit_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "visionstation.db");
    }

    private static void ExecSql(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<string> ReadTableNames(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
        using var rd = cmd.ExecuteReader();
        var names = new List<string>();
        while (rd.Read()) names.Add(rd.GetString(0));
        return names;
    }

    private static async Task<AuditRepository> NewRepoAsync(string dbPath)
    {
        var repo = new AuditRepository(dbPath);
        await repo.EnsureCreatedAsync();
        return repo;
    }

    /// <summary>
    /// 现场老库只有检测记录表，没有审计表。
    ///
    /// 这正是 EnsureCreated 的缺口：它对已存在的库什么都不做，
    /// 升级后第一条审计会抛 "no such table: AuditLogs"，
    /// 而开发机（删库重建）永远复现不出来。
    /// </summary>
    [Fact]
    public async Task EnsureCreated_OnLegacyDatabase_CreatesMissingAuditTable()
    {
        var dbPath = NewDbPath();

        // 造老库：只建检测记录表，且只建一列（缺的列由补列逻辑负责，这里只验补表）
        ExecSql(dbPath, "CREATE TABLE \"InspectionRecords\" (\"Id\" TEXT NOT NULL PRIMARY KEY);");

        var repo = new AuditRepository(dbPath);
        await repo.EnsureCreatedAsync();

        var tables = ReadTableNames(dbPath);
        Assert.Contains("AuditLogs", tables);

        // 老表必须还在 —— 补表不能顺手把原有数据清掉
        Assert.Contains("InspectionRecords", tables);
    }

    /// <summary>新建的库两种表都有（正常路径）</summary>
    [Fact]
    public async Task EnsureCreated_OnFreshDatabase_CreatesBothTables()
    {
        var dbPath = NewDbPath();
        await NewRepoAsync(dbPath);

        var tables = ReadTableNames(dbPath);
        Assert.Contains("InspectionRecords", tables);
        Assert.Contains("AuditLogs", tables);
    }

    /// <summary>写入后原样读回：字段一个都不能丢，尤其是前后值</summary>
    [Fact]
    public async Task SaveThenQuery_RoundTripsAllFields()
    {
        var repo = await NewRepoAsync(NewDbPath());
        var when = new DateTime(2026, 10, 24, 14, 32, 5);

        await repo.SaveAsync(new AuditEntry
        {
            Timestamp = when,
            UserName = "op01",
            UserRole = "Operator",
            Action = AuditAction.ParameterChange,
            Target = "尺寸公差",
            Before = "0.5",
            After = "0.65",
            Result = AuditResult.Success,
            Detail = "工程师改的",
        });

        var row = Assert.Single(await repo.QueryRecentAsync());
        Assert.True(row.Id > 0);
        Assert.Equal(when, row.Timestamp);
        Assert.Equal("op01", row.UserName);
        Assert.Equal("Operator", row.UserRole);
        Assert.Equal(AuditAction.ParameterChange, row.Action);
        Assert.Equal("尺寸公差", row.Target);
        Assert.Equal("0.5", row.Before);
        Assert.Equal("0.65", row.After);
        Assert.Equal("工程师改的", row.Detail);
    }

    /// <summary>
    /// 被拒绝的操作必须能存。
    ///
    /// 一条 Denied 说明有人在越界尝试 —— 它比十次成功操作更值得看。
    /// 如果 Result 只支持成功/失败两态，这条信息就永久丢了。
    /// </summary>
    [Fact]
    public async Task DeniedResult_IsPersistedAsDenied()
    {
        var repo = await NewRepoAsync(NewDbPath());

        await repo.SaveAsync(new AuditEntry
        {
            UserName = "op01",
            UserRole = "Operator",
            Action = AuditAction.ModeSwitch,
            Target = "垫圈A",
            Result = AuditResult.Denied,
            Detail = "权限不足：需要 Engineer",
        });

        var row = Assert.Single(await repo.QueryRecentAsync());
        Assert.Equal(AuditResult.Denied, row.Result);
    }

    /// <summary>按动作查"所有急停记录" —— 安全审查最常用的一条查询</summary>
    [Fact]
    public async Task QueryByAction_ReturnsOnlyMatchingRows()
    {
        var repo = await NewRepoAsync(NewDbPath());

        await repo.SaveAsync(new AuditEntry { Action = AuditAction.Start, UserName = "a" });
        await repo.SaveAsync(new AuditEntry { Action = AuditAction.EStopPressed, UserName = "b" });
        await repo.SaveAsync(new AuditEntry { Action = AuditAction.EStopPressed, UserName = "c" });

        var rows = await repo.QueryAsync(action: AuditAction.EStopPressed);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(AuditAction.EStopPressed, r.Action));
    }

    /// <summary>按用户查"这班是谁在操作"</summary>
    [Fact]
    public async Task QueryByUser_ReturnsOnlyThatUser()
    {
        var repo = await NewRepoAsync(NewDbPath());

        await repo.SaveAsync(new AuditEntry { Action = AuditAction.Start, UserName = "op01" });
        await repo.SaveAsync(new AuditEntry { Action = AuditAction.Stop, UserName = "op02" });

        var rows = await repo.QueryAsync(userName: "op01");

        Assert.Single(rows);
        Assert.Equal("op01", rows[0].UserName);
    }

    /// <summary>
    /// 超长字段截断而不是抛异常。
    ///
    /// 审计写失败会连着丢掉整条记录；截断至少保住了"谁、何时、做了什么"。
    /// 异常详情可能很长，这条一定会被触发。
    /// </summary>
    [Fact]
    public async Task OverlongFields_AreTrimmedNotThrown()
    {
        var repo = await NewRepoAsync(NewDbPath());

        await repo.SaveAsync(new AuditEntry
        {
            Action = AuditAction.EngineReload,
            Target = new string('x', 400),
            Detail = new string('y', 900),
        });

        var row = Assert.Single(await repo.QueryRecentAsync());
        Assert.Equal(120, row.Target.Length);
        Assert.Equal(500, row.Detail!.Length);
    }

    /// <summary>服务层：Record 之后 Flush，数据必须落库，且身份取自委托</summary>
    [Fact]
    public async Task ServiceRecord_AfterFlush_IsPersisted()
    {
        var dbPath = NewDbPath();
        var repo = await NewRepoAsync(dbPath);
        var svc = new AuditService(repo, identity: () => ("engineer01", "Engineer"));

        svc.Record(AuditAction.EngineReload, "VisionMaster", detail: "换方案后重载");
        svc.Flush(TimeSpan.FromSeconds(5));

        var row = Assert.Single(await repo.QueryRecentAsync());
        Assert.Equal("engineer01", row.UserName);
        Assert.Equal("Engineer", row.UserRole);
        Assert.Equal(AuditAction.EngineReload, row.Action);
        Assert.Equal("VisionMaster", row.Target);
    }

    /// <summary>身份取不到也不能丢审计：至少记下动作发生过</summary>
    [Fact]
    public async Task ServiceRecord_WhenIdentityThrows_StillPersistsWithUnknown()
    {
        var repo = await NewRepoAsync(NewDbPath());
        var svc = new AuditService(repo, identity: () => throw new InvalidOperationException("会话没了"));

        svc.Record(AuditAction.Stop, "垫圈A");
        svc.Flush(TimeSpan.FromSeconds(5));

        var row = Assert.Single(await repo.QueryRecentAsync());
        Assert.Equal("unknown", row.UserName);
        Assert.Equal(AuditAction.Stop, row.Action);
    }

    private sealed class ThrowingRepository : IAuditRepository
    {
        public int SaveAttempts;

        public Task EnsureCreatedAsync() => Task.CompletedTask;

        public Task SaveAsync(AuditEntry entry)
        {
            Interlocked.Increment(ref SaveAttempts);
            throw new InvalidOperationException("数据库被锁定");
        }

        public Task<IReadOnlyList<AuditEntry>> QueryRecentAsync(int count = 200)
            => Task.FromResult<IReadOnlyList<AuditEntry>>(Array.Empty<AuditEntry>());

        public Task<IReadOnlyList<AuditEntry>> QueryAsync(
            AuditAction? action = null, string? userName = null, DateTime? since = null, int count = 200)
            => Task.FromResult<IReadOnlyList<AuditEntry>>(Array.Empty<AuditEntry>());

        public Task<int> DeleteOlderThanAsync(DateTime cutoff) => Task.FromResult(0);
    }

    /// <summary>
    /// 仓储一直失败时：Record 不抛、不阻塞，Flush 也不抛。
    ///
    /// 审计是旁路。它挂了却把异常抛进"停止检测"的按钮命令里，
    /// 等于用一个记账功能把产线停了 —— 这是最不该发生的连锁反应。
    /// </summary>
    [Fact]
    public void Record_WhenRepositoryAlwaysThrows_DoesNotThrow()
    {
        var repo = new ThrowingRepository();
        var svc = new AuditService(repo, identity: () => ("op01", "Operator"));

        for (var i = 0; i < 5; i++)
            svc.Record(AuditAction.Start, "垫圈A");

        var thrown = false;
        try
        {
            svc.Flush(TimeSpan.FromSeconds(3));
        }
        catch
        {
            thrown = true;
        }

        Assert.False(thrown);
        Assert.True(repo.SaveAttempts > 0, "后台消费线程应当至少尝试过写入");
    }
}
