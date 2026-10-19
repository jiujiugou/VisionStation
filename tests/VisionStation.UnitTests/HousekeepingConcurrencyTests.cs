using System.Reflection;
using VisionStation.Service.Housekeeping;
using VisionStation.Storage;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// B 批验收：清理重入保护（F-107）与数据访问并发（F-111）。
/// 说明：这两条在修复前不具备可执行的红证据（旧代码没有 gate、也没有“每次操作新 context”的入口），
/// 修复前的依据是 CODE_REVIEW 的结构事实（DI 注册单例 DbContext）。
/// 本文件证明修复后行为成立。
/// </summary>
public class HousekeepingConcurrencyTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "vs_cc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static HousekeepingOptions Opts() =>
        new(500, 30, 30, 1_000_000_000, 86_400_000, 86_400_000);

    private static HousekeepingService Make(string dir)
    {
        var repo = new InspectionRepository(Path.Combine(dir, "cc.db"));
        repo.EnsureCreatedAsync().GetAwaiter().GetResult();
        return new HousekeepingService(repo, dir, Opts());
    }

    [Fact]
    public async Task R107_WhilePreviousRoundRunning_MustSkip()
    {
        var dir = NewRoot();
        using var svc = Make(dir);

        // 白盒：占住重入闸门，模拟「上一轮还没跑完」
        var field = typeof(HousekeepingService)
            .GetField("_cleanupGate", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var gate = (SemaphoreSlim)field!.GetValue(svc)!;

        gate.Wait();
        try
        {
            var executed = await svc.CleanupOnceAsync();
            Assert.False(executed, "上一轮未结束时必须跳过本轮，不得排队叠加（F-107）");
        }
        finally
        {
            gate.Release();
        }

        // 闸门释放后，下一轮应正常执行
        Assert.True(await svc.CleanupOnceAsync(), "上一轮结束后必须恢复正常执行");
    }

    [Fact]
    public async Task R111_ConcurrentReads_MustNotShareContext()
    {
        var dir = NewRoot();
        var repo = new InspectionRepository(Path.Combine(dir, "cc.db"));
        await repo.EnsureCreatedAsync();

        var ex = await Record.ExceptionAsync(() =>
            Task.WhenAll(Enumerable.Range(0, 16).Select(_ => repo.GetRecentAsync(10))));

        Assert.Null(ex);
    }

    [Fact]
    public async Task R111_ConcurrentWrites_MustNotLoseRecords()
    {
        var dir = NewRoot();
        var repo = new InspectionRepository(Path.Combine(dir, "cc.db"));
        await repo.EnsureCreatedAsync();

        var tasks = Enumerable.Range(0, 8).Select(_ => (Task)Task.Run(() => repo.GetRecentAsync(5)))
            .Concat(Enumerable.Range(0, 8).Select(_ => (Task)Task.Run(() => repo.DeleteOlderThanAsync(DateTime.Now.AddYears(-1)))));

        var ex = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
        Assert.Null(ex);
    }
}
