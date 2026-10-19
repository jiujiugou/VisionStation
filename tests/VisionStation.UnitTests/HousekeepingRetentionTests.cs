using VisionStation.Service.Housekeeping;
using VisionStation.Storage;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// 三步失效分析法 · 阶段3：运维模块 A 批修复的验收测试。
/// 按目标行为编写，当前代码不满足即红；断言不随实现调整。
/// 运行：dotnet test tests/VisionStation.UnitTests --filter FullyQualifiedName~HousekeepingRetentionTests
/// </summary>
public class HousekeepingRetentionTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "vs_hk_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static HousekeepingOptions Opts(int keepDays = 30, int imageDays = 30,
        long maxBytes = 1_000_000_000, int maxMemMb = 500) =>
        new(maxMemMb, keepDays, imageDays, maxBytes, 86_400_000, 86_400_000);

    /// <summary>Timer 间隔设成 1 天，构造后不会被自动触发，清理只能由测试显式驱动。</summary>
    private static HousekeepingService Make(string imageDir, HousekeepingOptions options)
    {
        var dbPath = Path.Combine(Path.GetDirectoryName(imageDir)!, "hk.db");
        var repo = new InspectionRepository(new InspectionDbContext(dbPath));
        repo.EnsureCreatedAsync().GetAwaiter().GetResult();
        return new HousekeepingService(repo, imageDir, options);
    }

    private static string Put(string dir, string name, int bytes, int daysAgo)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, new byte[bytes]);
        File.SetLastWriteTime(p, DateTime.Now.AddDays(daysAgo));
        return p;
    }

    // ── 配置阈值（F-113） ─────────────────────────────────────────

    [Fact]
    public void R113_ZeroImageRetentionDays_MustThrow()
    {
        var dir = NewRoot();
        var ex = Record.Exception(() =>
        {
            using var svc = Make(dir, Opts(imageDays: 0));
        });

        Assert.IsType<ArgumentOutOfRangeException>(ex);
    }

    [Fact]
    public void R113_ZeroKeepRecordsDays_MustThrow()
    {
        var dir = NewRoot();
        var ex = Record.Exception(() =>
        {
            using var svc = Make(dir, Opts(keepDays: 0));
        });

        Assert.IsType<ArgumentOutOfRangeException>(ex);
    }

    [Fact]
    public void R106_ImageRetentionShorterThanRecords_MustThrow()
    {
        var dir = NewRoot();
        var ex = Record.Exception(() =>
        {
            using var svc = Make(dir, Opts(keepDays: 30, imageDays: 7));
        });

        Assert.IsType<ArgumentOutOfRangeException>(ex);
    }

    // ── 单文件异常隔离（F-109） ──────────────────────────────────

    [Fact]
    public async Task R109_OneUndeletableFile_MustNotAbortWholeRound()
    {
        var dir = NewRoot();
        var locked = Put(dir, "000_locked.bmp", 1024, -40);
        var normal = Put(dir, "999_expired.bmp", 1024, -40);
        File.SetAttributes(locked, FileAttributes.ReadOnly);   // 删除会抛 UnauthorizedAccessException

        try
        {
            using var svc = Make(dir, Opts(imageDays: 30));

            var executed = await svc.CleanupOnceAsync();

            Assert.True(executed);
            Assert.False(File.Exists(normal),
                "一个文件删不掉就中止整轮，后面的过期文件没被清理（F-109）");
        }
        finally
        {
            if (File.Exists(locked)) File.SetAttributes(locked, FileAttributes.Normal);
        }
    }

    // ── 保留策略基线（回归保护） ─────────────────────────────────

    [Fact]
    public async Task R105_ExpiredDeleted_LiveKept()
    {
        var dir = NewRoot();
        var expired = Put(dir, "old.bmp", 2048, -40);
        var live = Put(dir, "live.bmp", 2048, -1);

        using var svc = Make(dir, Opts(imageDays: 30));
        await svc.CleanupOnceAsync();

        Assert.False(File.Exists(expired), "超过保留期的图片必须删除");
        Assert.True(File.Exists(live), "保留期内的图片不得删除");
    }

    [Fact]
    public async Task R105_OverCapacity_DeletesOldestFirst()
    {
        var dir = NewRoot();
        var oldest = Put(dir, "a.bmp", 400_000, -3);
        var middle = Put(dir, "b.bmp", 400_000, -2);
        var newest = Put(dir, "c.bmp", 400_000, -1);

        using var svc = Make(dir, Opts(imageDays: 30, maxBytes: 900_000));
        await svc.CleanupOnceAsync();

        Assert.False(File.Exists(oldest), "超容时应从最旧开始删");
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
    }
}
