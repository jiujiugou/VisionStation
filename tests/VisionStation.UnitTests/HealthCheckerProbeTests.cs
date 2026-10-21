using Microsoft.Data.Sqlite;
using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Service;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// R-102 / R-103 正向用例：修复后的探测不得把「真的可用」误判为故障。
/// 与 HealthCheckerContractTests 里的反例成对存在——只有反例通过，说明是误杀，不是修复。
/// </summary>
public class HealthCheckerProbeTests
{
    private static string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs_probe_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void R102_RealSqliteFile_MustPass()
    {
        var dir = NewRoot();
        var db = Path.Combine(dir, "real.db");

        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE T(Id INTEGER)";
            cmd.ExecuteNonQuery();
        }

        var hc = new HealthChecker(new DeviceManager(), db, dir, "", null);
        var r = hc.RunAll().First(x => x.Item == "数据库");

        Assert.True(r.OK, $"真实可用的 SQLite 库被误判：{r.Detail} / {r.Error}");
    }

    [Fact]
    public void R103_RealBmpHeader_MustPass()
    {
        var dir = NewRoot();
        var tpl = Path.Combine(dir, "real_template.bmp");

        // 最小 BMP 头：'B','M' + 文件长度 + 保留字段 + 像素数据偏移（共 14 字节头）
        var bytes = new byte[54 + 16];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        File.WriteAllBytes(tpl, bytes);

        var hc = new HealthChecker(new DeviceManager(), Path.Combine(dir, "a.db"), dir, tpl, null);
        var r = hc.RunAll().First(x => x.Item == "模板文件");

        Assert.True(r.OK, $"真实 BMP 被误判：{r.Detail} / {r.Error}");
    }

    [Fact]
    public void R103_TextFileRenamedToBmp_MustFail()
    {
        var dir = NewRoot();
        var tpl = Path.Combine(dir, "fake_template.bmp");
        File.WriteAllText(tpl, "plain text, not an image");

        var hc = new HealthChecker(new DeviceManager(), Path.Combine(dir, "a.db"), dir, tpl, null);
        var r = hc.RunAll().First(x => x.Item == "模板文件");

        Assert.False(r.OK, "文本文件改扩展名必须被判为不可用");
    }

    [Fact]
    public void 设备离线_自检带出每台的原因而不是一句有设备离线()
    {
        var dir = NewRoot();
        var dm = new DeviceManager();
        dm.Register(new CameraDevice(new OfflineCamera()));

        var hc = new HealthChecker(dm, Path.Combine(dir, "a.db"), dir, "", null);

        var item = hc.RunAll().Single(x => x.Item == "设备连接");
        Assert.False(item.OK);
        Assert.Contains("被独占", item.Error);
        Assert.DoesNotContain("有设备离线", item.Error);
    }

    /// <summary>离线且带原因的相机替身（模拟 HikCamera 被 0x80000203 拒绝）</summary>
    private sealed class OfflineCamera : ICamera
    {
        public string? LastError { get; } = "打开设备被拒（0x80000203 设备被独占/无数据源）";
        public bool IsAvailable => false;
        public int FrameCount => 0;
        public void Open() { }
        public void Close() { }
        public CameraFrame? Capture() => null;
        public void Dispose() { }
    }
}
