using Microsoft.Data.Sqlite;
using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Service;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// R-102 / R-103 正向用例：修复后的探测不得把「真的可用」误判为故障。
/// 2026-10-23：模板文件的正例换成引擎资源（.sol / ONNX 模型），与反例文件保持一致。
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

        var hc = new HealthChecker(new DeviceManager(), db, dir, null, dir);
        var r = hc.RunAll().First(x => x.Item == "数据库");

        Assert.True(r.OK, $"真实可用的 SQLite 库被误判：{r.Detail} / {r.Error}");
    }

    [Fact]
    public void R103_RealModelFile_MustPass()
    {
        var dir = NewRoot();
        var model = Path.Combine(dir, "neu-det.onnx");
        File.WriteAllBytes(model, new byte[] { 0x08, 0x07, 0x12, 0x00 });   // 非空模型文件

        var recipe = new RecipeConfig { Name = "ONNX 配方", Engine = EngineNames.Onnx };
        recipe.Onnx.ModelPath = model;

        var hc = new HealthChecker(new DeviceManager(), Path.Combine(dir, "a.db"), dir, recipe, dir);
        var r = hc.RunAll().First(x => x.Item == "检测引擎");

        Assert.True(r.OK, $"真实存在的模型被误判：{r.Detail} / {r.Error}");
    }

    [Fact]
    public void R103_MissingModel_MustFail()
    {
        var dir = NewRoot();

        var recipe = new RecipeConfig { Name = "ONNX 配方", Engine = EngineNames.Onnx };
        recipe.Onnx.ModelPath = Path.Combine(dir, "not-exist.onnx");

        var hc = new HealthChecker(new DeviceManager(), Path.Combine(dir, "a.db"), dir, recipe, dir);
        var r = hc.RunAll().First(x => x.Item == "检测引擎");

        Assert.False(r.OK, "模型缺失必须 FAIL：引擎在启动期就该被指出来");
    }

    [Fact]
    public void 设备离线_自检带出每台的原因而不是一句有设备离线()
    {
        var dir = NewRoot();
        var dm = new DeviceManager();
        dm.Register(new OfflineDevice());

        var hc = new HealthChecker(dm, Path.Combine(dir, "a.db"), dir, null, dir);

        var item = hc.RunAll().Single(x => x.Item == "设备连接");
        Assert.False(item.OK);
        Assert.Contains("被独占", item.Error);
        Assert.DoesNotContain("有设备离线", item.Error);
    }

    /// <summary>离线且带原因的通用设备替身（自检只需要"离线 + 有原因"这两件事）</summary>
    private sealed class OfflineDevice : IDevice
    {
        public string Name => "相机";
        public string Type => "Camera";
        public bool IsConnected => false;
        public string Status => "离线";
        public string? LastError { get; } = "打开设备被拒（0x80000203 设备被独占/无数据源）";
        public void Connect() { }
        public void Disconnect() { }
    }
}
