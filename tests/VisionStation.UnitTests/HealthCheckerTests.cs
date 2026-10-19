using VisionStation.Devices;
using VisionStation.Service;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// 三步失效分析法 · 阶段3 单元测试（运维模块切片）
///
/// 每个测试对应 RequirementCatalog 中的一条约束，命名 R-xxx_行为。
/// 这些测试按「目标行为」编写：当前代码若不满足约束则红，
/// 修复后转绿 —— 红本身就是缺陷证据（阶段3 的 FAIL 态）。
/// </summary>
public class HealthCheckerTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "vs_hc_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        _tempDirs.Add(d);
        return d;
    }

    private HealthChecker Make(string? dbPath, DeviceManager? devices = null)
        => new(devices ?? new DeviceManager(), dbPath!, NewDir(), "", null);

    /// <summary>R-017：设备表为空不得判「全部在线」——0==0 是判据失效，必须显式 FAIL。</summary>
    [Fact]
    public void R017_EmptyDeviceTable_MustNotReportAllOnline()
    {
        var hc = Make(dbPath: Path.Combine(NewDir(), "vs.db"));   // 未注册任何设备
        var devices = hc.RunAll().First(r => r.Item == "设备连接");
        Assert.False(devices.OK, "空设备表被报为『全部在线』（F-038：0==0 判据失效）");
    }

    /// <summary>R-001：数据库目录不可写时自检必须 FAIL——判据必须可探伪，不能只看目录存在。</summary>
    [Fact]
    public void R001_UnwritableDbDirectory_MustFail()
    {
        var dir = NewDir();
        var probe = Path.Combine(dir, ".db_write_test");
        using var lockStream = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None);
        var hc = Make(dbPath: Path.Combine(dir, "visionstation.db"));
        var db = hc.RunAll().First(r => r.Item == "数据库");
        Assert.False(db.OK, $"目录被锁定仍报可写（F-001：判据只查存在性不查可用性）。Detail={db.Detail}");
    }

    /// <summary>R-001 反向用例：文件不存在但目录可写 → 必须 OK（SQLite 首次运行自动建库）。</summary>
    [Fact]
    public void R001_AbsentDbFile_WritableDirectory_MustPass()
    {
        var hc = Make(dbPath: Path.Combine(NewDir(), "visionstation.db"));
        var db = hc.RunAll().First(r => r.Item == "数据库");
        Assert.True(db.OK, "可写目录下缺库文件是正常初始状态，不得误报");
    }

    /// <summary>R-004：单项检查异常不得中断其余检查——RunAll 必须隔离故障项。</summary>
    [Fact]
    public void R004_SingleCheckCrash_OthersMustStillReturn()
    {
        var hc = Make(dbPath: null!);   // null 路径 → CheckDatabase 内部抛参数异常
        var results = hc.RunAll();      // 修复前：整个 RunAll 直接抛；修复后：返回 5 项
        Assert.Equal(5, results.Count);
        Assert.False(results.First(r => r.Item == "数据库").OK);
        Assert.True(results.First(r => r.Item == "存储路径").OK, "数据库项崩溃不应拖垮其余检查");
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { /* 留给系统清理 */ }
        }
    }
}
