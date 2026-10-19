using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenCvSharp;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;
using VisionStation.Storage;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// Storage 切片（F-S01..F-S06）的防线用例。
/// 每条对应 RequirementCatalog 里的一条约束，命令写在约束的 command 字段里。
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
        var r = new InspectionReport { TriggerSource = trigger };
        r.SetToolResult("Presence", ToolResult.Ok("有"));
        r.SetToolResult("Dimension", ToolResult.Ok("尺寸合格"));
        r.SetToolResult("Defect", ToolResult.Ok("无缺陷"));
        return r;
    }

    private static CameraFrame MakeFrame(string path)
        => new()
        {
            Image = new Mat(16, 16, MatType.CV_8UC3, Scalar.All(0)),
            Timestamp = DateTime.Now,
            FilePath = path
        };

    private static List<string> ReadColumns(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(\"InspectionRecords\");";
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

    // ── R-S01：存图路径必须写回报告 ────────────────────────────────
    [Fact]
    public void R_S01_SavedPathIsWrittenBack()
    {
        var root = NewRoot();
        var svc = new ImageStoreService(root);
        var report = MakeReport();
        var frame = MakeFrame("HikCamera_Frame_12");

        var result = svc.Save(frame, report);

        Assert.True(result.IsSaved, result.Error);
        Assert.Equal(result.Path, report.ImagePath);
        Assert.True(File.Exists(report.ImagePath), "记录里的路径必须指向真实存在的文件");
        Assert.StartsWith(root, report.ImagePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void R_S01_SkippedFrame_DoesNotTouchImagePath()
    {
        var svc = new ImageStoreService(NewRoot());
        svc.SetMode(ImageStoreMode.NGOnly);

        var report = MakeReport();          // 全 OK → NGOnly 下不存
        report.ImagePath = "原图来源";
        var result = svc.Save(MakeFrame("src"), report);

        Assert.Equal(ImageSaveOutcome.Skipped, result.Outcome);
        Assert.Equal("原图来源", report.ImagePath);
    }

    // ── R-S02：全部工具结果都要落库 ────────────────────────────────
    [Fact]
    public async Task R_S02_AllToolResultsPersisted()
    {
        var dbPath = Path.Combine(NewRoot(), "s02.db");
        var repo = new InspectionRepository(dbPath);
        await repo.EnsureCreatedAsync();

        var report = MakeReport();
        report.SetToolResult("Yolo", ToolResult.Ng("孔位缺失"));   // 第四个工具

        await repo.SaveAsync(report);

        var rows = await repo.GetRecentAsync(1);
        Assert.Single(rows);
        Assert.Equal("NG", rows[0].OverallVerdict);

        using var doc = System.Text.Json.JsonDocument.Parse(rows[0].ToolResultsJson);
        var props = doc.RootElement.EnumerateObject().ToList();
        Assert.Equal(4, props.Count);
        Assert.False(doc.RootElement.GetProperty("Yolo").GetProperty("IsOk").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("Presence").GetProperty("IsOk").GetBoolean());
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

        var rows = await v2.GetRecentAsync(10);
        Assert.Single(rows);                              // 旧数据没被清掉
        Assert.Equal("补列前", rows[0].TriggerSource);
        // 补列默认值是空串，不是 "{}"：旧行本来就没有这份快照，
        // 写成 "{}" 会让人误以为「当时确实一个工具都没跑」。空串如实表示「这一版之前没有这个字段」。
        Assert.Equal("", rows[0].ToolResultsJson);

        // 补列之后能正常写入
        await v2.SaveAsync(MakeReport("补列后"));
        Assert.Equal(2, (await v2.GetRecentAsync(10)).Count);
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

    // ── R-S05：文件名对同一毫秒的不同报告唯一 ──────────────────────
    [Fact]
    public void R_S05_FileNameUniquePerReport()
    {
        var at = new DateTime(2026, 10, 19, 12, 0, 0, 123);
        var id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var a = ImageStoreService.BuildFileName(at, "NG", "cap", id1);
        var b = ImageStoreService.BuildFileName(at, "NG", "cap", id2);

        Assert.NotEqual(a, b);
        Assert.Equal(a, ImageStoreService.BuildFileName(at, "NG", "cap", id1));  // 同报告重存幂等
        Assert.StartsWith("120000123_NG_cap_", a, StringComparison.Ordinal);
    }

    // ── R-S06：抽样率为 0 在配置入口就被拦下 ───────────────────────
    [Fact]
    public void R_S06_SamplingRateValidated()
    {
        var svc = new ImageStoreService(NewRoot());

        Assert.Throws<ArgumentOutOfRangeException>(() => svc.SetMode(ImageStoreMode.Sampling, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => svc.SetMode(ImageStoreMode.Sampling, -1));

        svc.SetMode(ImageStoreMode.Sampling, 1);
        var frame = MakeFrame("f");
        for (var i = 0; i < 10; i++)
        {
            var r = svc.Save(frame, MakeReport($"s{i}"));
            Assert.NotEqual(ImageSaveOutcome.Failed, r.Outcome);
        }
    }
}
