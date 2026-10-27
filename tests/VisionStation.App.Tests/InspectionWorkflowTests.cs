using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using VisionStation.App.Services.Imaging;
using VisionStation.App.Services.Inspection;
using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Devices.Plc;

namespace VisionStation.App.Tests;

/// <summary>
/// 工作流的"主动检测"契约。
///
/// 2026-10-26 相机层删除后，本程序不再采图：图像由平台产生、结果由平台推送。
/// 所以 <c>RunOnceAsync</c> 只剩一件必须守住的事 —— **什么都不做，且绝不入库**。
/// 自动循环仍会按节拍调进来，若每次造一条结果，良率统计就被污染了。
///
/// 原先的四个用例（采不到图、正常一帧、存图失败、取消）测的是已经不存在的采集路径，
/// 随相机层一起删除；替代它们的是下面的"空转且不落库"。
///
/// <para>
/// 另一半（本文件的重点）是 <c>AcceptExternalAsync</c> —— VM 推来一帧的唯一入口。
/// 它按固定顺序做四件事：<b>解析图 → 读图/标注 → 入库 → 回写 PLC</b>。
/// 下面每条用例盯的都是"某一步失败把整帧带崩"或"某一步的结果没落到它该落的地方"，
/// 而不是"返回值的形状对不对"。这四步里只有"入库"会抛，其余失败只记原因，
/// 因为少一张图是次要问题、少一条判定记录才是质量事故。
/// </para>
/// </summary>
public class InspectionWorkflowTests
{
    [Fact]
    public async Task 主动检测_永远返回null且不入库()
    {
        var repo = new FakeInspectionRepository();
        using var workflow = new InspectionWorkflow(
            new FakeEngine(), repo, NullLogger<InspectionWorkflow>.Instance);

        var result = await workflow.RunOnceAsync(TriggerOrigin.Manual, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, repo.SaveCount);
    }

    /// <summary>
    /// 最顺的那一条：报文给的文件能读，入库一次、两张图都在。
    ///
    /// 它防的是"图读出来了但只给了原图"——界面上有图、看不出 NG 标在哪，
    /// 现场就会开始怀疑判定本身。原图与标注图必须同时产出，缺一张等于这条链没跑完。
    /// </summary>
    [Fact]
    public async Task 推送一帧_带可读文件_入库且两张图都在()
    {
        var dir = NewTempDir();
        var path = WriteBmp(dir, "frame-ok.bmp", width: 64, height: 48);

        var repo = new FakeInspectionRepository();
        using var workflow = new InspectionWorkflow(
            new FakeEngine(), repo, NullLogger<InspectionWorkflow>.Instance);

        var report = OkReport(path);

        var result = await workflow.AcceptExternalAsync(report, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, repo.SaveCount);
        Assert.NotNull(result!.OriginalImage);
        Assert.NotNull(result.AnnotatedImage);
        Assert.True(result.ImageSaved);
        Assert.Null(result.ImageUnavailableReason);
    }

    /// <summary>
    /// 报文没带路径：照样入库，只是把"为什么没图"说清楚。
    ///
    /// 这是整条链路最容易写错的一处：把入库放在读图后面，一旦平台没配存图，
    /// 每一件产品都会"测了但库里没有"。良率统计直接少一批数据，
    /// 而界面上只表现为一块黑屏 —— 没人会去怀疑是入库没跑。
    /// </summary>
    [Fact]
    public async Task 推送一帧_没带路径_仍然入库但给出原因()
    {
        var repo = new FakeInspectionRepository();
        using var workflow = new InspectionWorkflow(
            new FakeEngine(), repo, NullLogger<InspectionWorkflow>.Instance);

        var report = OkReport(imagePath: string.Empty);

        var result = await workflow.AcceptExternalAsync(report, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, repo.SaveCount);
        Assert.Null(result!.OriginalImage);
        Assert.False(string.IsNullOrWhiteSpace(result.ImageUnavailableReason));
    }

    /// <summary>
    /// 报文给的是目录（VM「输出图像」最常出现的形态）：取目录里最新一张，并把路径写回报告。
    ///
    /// 回写这一步才是重点。不写回的话，界面显示的是刚找到的那张图，
    /// 库里记的却是一个目录路径 —— 事后点开历史记录只能看到"打不开"，
    /// 或者更糟：被别人按文件名规则解释成另一件产品的图。
    /// </summary>
    [Fact]
    public async Task 推送一帧_报文给目录_取最新一张并回写报告路径()
    {
        var dir = NewTempDir();
        var older = WriteBmp(dir, "older.bmp");
        var newer = WriteBmp(dir, "newer.bmp");

        // 显式拉开写盘时间，避免靠"谁先写"决定谁新 —— 那在文件系统时间粒度粗的机器上会翻车
        File.SetLastWriteTime(older, DateTime.Now.AddMinutes(-10));
        File.SetLastWriteTime(newer, DateTime.Now.AddSeconds(-1));

        var repo = new FakeInspectionRepository();
        using var workflow = new InspectionWorkflow(
            new FakeEngine(), repo, NullLogger<InspectionWorkflow>.Instance);

        var report = OkReport(imagePath: dir);

        var result = await workflow.AcceptExternalAsync(report, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.ImageSaved);

        var saved = repo.LastSaved!.ImagePath;
        Assert.Equal(newer, saved, ignoreCase: true);
        Assert.True(File.Exists(saved), "库里记的路径必须指向一个真实存在的文件");
        Assert.False(Directory.Exists(saved), "库里记的必须是文件，不能还是那个目录");
    }

    /// <summary>
    /// 路径存在但内容不是图：解码失败只记原因，**不能把这一帧也丢掉**。
    ///
    /// 现场最常见的成因是 VM 写盘到一半、或把别的输出错配成了 imagePath。
    /// 如果这里抛出去，收包侧那个 catch 会把它变成一条 Error 日志，
    /// 表现为"这件产品凭空消失了"—— 比一条 NG 难查得多。
    /// </summary>
    [Fact]
    public async Task 推送一帧_文件内容不是图_解码失败仍然入库()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "not-an-image.bmp");
        File.WriteAllText(path, "这不是位图，只是一段文本");

        var repo = new FakeInspectionRepository();
        using var workflow = new InspectionWorkflow(
            new FakeEngine(), repo, NullLogger<InspectionWorkflow>.Instance);

        var report = OkReport(path);

        var result = await workflow.AcceptExternalAsync(report, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, repo.SaveCount);
        Assert.Null(result!.OriginalImage);

        // 不锁死文案：这里只要求"说得出这张图没法用"，具体写"解码失败"还是"读取失败"由实现决定
        var reason = result.ImageUnavailableReason;
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.True(
            reason!.Contains("解码") || reason.Contains("无法") || reason.Contains("失败"),
            $"解码失败必须留下可归因的原因，实际是：{reason}");
    }

    /// <summary>
    /// 兜底目录：报文路径不可用时改从设置里的目录取，并把兜底那张写回报告。
    ///
    /// 这条覆盖的是"平台只发相对路径/发了个已删文件"的现场。关键在于路径解析与入库发生在
    /// 同一层 —— 若把兜底放界面，会出现"界面显示兜底图、库里记的仍是那个无效路径"的两份真相。
    /// </summary>
    [Fact]
    public async Task 推送一帧_报文路径不可用_从兜底目录取图并回写()
    {
        var dir = NewTempDir();
        var fallback = WriteBmp(dir, "fallback.bmp");

        // 属性名大小写敏感（JsonOptions 没开 PropertyNameCaseInsensitive），
        // 写成 folderFallbackEnabled 会被静默当成"未启用"——设置界面看着开了、实际不生效。
        var settingsPath = Path.Combine(dir, "image-source.json");
        File.WriteAllText(settingsPath,
            $$"""
            {
              "FolderFallbackEnabled": true,
              "Folder": {{JsonSerializer.Serialize(dir)}},
              "Recursive": true
            }
            """);

        var settings = new ImageSourceSettingsStore(settingsPath);
        var repo = new FakeInspectionRepository();
        using var workflow = new InspectionWorkflow(
            new FakeEngine(), repo, NullLogger<InspectionWorkflow>.Instance,
            imageSourceSettings: settings);

        var missing = Path.Combine(dir, "vm-never-wrote-this.bmp");
        var report = OkReport(missing);

        var result = await workflow.AcceptExternalAsync(report, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.ImageSaved);

        Assert.Equal(fallback, report.ImagePath, ignoreCase: true);
        Assert.Equal(fallback, repo.LastSaved!.ImagePath, ignoreCase: true);
    }

    /// <summary>
    /// 一项结论都没有的帧必须判 NG 且入库。
    ///
    /// 空结果集是"引擎一项都没跑"，不是"全部通过"。这条同时守住两件事：
    /// 报告自己的 IsOk（空集合上 All 恒为 true 的经典坑），
    /// 以及工作流不能因为"没什么可存"就把这一帧吞掉 —— 吞掉之后良率分母也少了。
    /// </summary>
    [Fact]
    public async Task 推送一帧_空结果集_入库且判NG()
    {
        var repo = new FakeInspectionRepository();
        using var workflow = new InspectionWorkflow(
            new FakeEngine(), repo, NullLogger<InspectionWorkflow>.Instance);

        var report = new InspectionReport
        {
            Timestamp = DateTime.Now,
            TriggerSource = "VM",
            Engine = EngineNames.Vm,
            ImagePath = string.Empty
        };

        var result = await workflow.AcceptExternalAsync(report, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, repo.SaveCount);
        Assert.False(repo.LastSaved!.IsOk);
        Assert.Equal("NG", result!.Report.OverallVerdict);
    }

    /// <summary>
    /// 标注真的画在了图上。
    ///
    /// 这是 <c>DrawAnnotations</c> 唯一的证据：它画在缩放到上屏宽度**之前**，
    /// 顺序一颠倒，框就会跑到零件外面（缩得越小偏得越多），而图片数量、尺寸全都正常，
    /// 只有看图的人能发现。所以这里必须比像素，不能只比"两张图不是同一个对象"。
    /// </summary>
    [Fact]
    public async Task 推送一帧_带矩形标注_标注图与原图像素不同()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "black.bmp");
        using (var black = new Mat(240, 320, MatType.CV_8UC3, Scalar.All(0)))
        {
            Assert.True(Cv2.ImWrite(path, black), $"测试底图没写出来：{path}");
        }

        var repo = new FakeInspectionRepository();
        using var workflow = new InspectionWorkflow(
            new FakeEngine(), repo, NullLogger<InspectionWorkflow>.Instance);

        var report = OkReport(path);
        report.SetToolResult("缺陷", ToolResult.Ok("表面无缺陷", new List<DrawCommand>
        {
            new DrawRect
            {
                Rect = new OpenCvSharp.Rect(80, 60, 160, 120),   // 覆盖图中间一大片
                Color = Scalar.All(255),                         // 纯白，和纯黑底对比最大
                Thickness = 3
            }
        }));

        var result = await workflow.AcceptExternalAsync(report, CancellationToken.None);

        Assert.NotNull(result);
        var original = result!.OriginalImage;
        var annotated = result.AnnotatedImage;
        Assert.NotNull(original);
        Assert.NotNull(annotated);

        // 32×24 的图会被缩到 1280 宽；320×240 在阈值内不缩放，两张尺寸必须一致才能逐像素比
        Assert.Equal(original!.PixelWidth, annotated!.PixelWidth);
        Assert.Equal(original.PixelHeight, annotated.PixelHeight);

        var before = Pixels(original);
        var after = Pixels(annotated);

        var diff = 0;
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i] != after[i]) diff++;
        }

        Assert.True(diff > 0, "标注图与原图逐字节相同 —— Overlays 一条都没被执行");

        // 光"不一样"还不够：OK/NG 水印本来就画在左上角，即使 Overlays 被整个忽略也会不一样。
        // 所以再钉一次"框落在它该在的位置"：矩形边框经过的那一圈必须是白的。
        var stride = annotated.PixelWidth * 3;
        var brightInRect = 0;
        for (var y = 60; y < 180; y++)
        {
            for (var x = 80; x < 240; x++)
            {
                var i = y * stride + x * 3;
                if (after[i] > 200 && after[i + 1] > 200 && after[i + 2] > 200) brightInRect++;
            }
        }

        Assert.True(brightInRect > 0, "矩形区域里一个亮点都没有 —— 框没画在声明的坐标上");
    }

    /// <summary>
    /// PLC 回写：OK 写 1、NG 写 2，地址是构造时给的那个结果寄存器。
    ///
    /// 这条防的是"判定归 VM、回写没人做"——改造前 VM 推送这条路径压根没有回写，
    /// 真上产线表现为 PLC 永远收不到 OK/NG，整机卡在等结果。
    /// 后台消费是异步的，所以断言用轮询等，不锁死先后顺序。
    /// </summary>
    [Fact]
    public async Task 推送一帧_有PLC_分别回写OK与NG编码()
    {
        var plc = new RecordingPlc();
        var address = new PlcAddress(ModbusArea.HoldingRegister, 101);

        var repo = new FakeInspectionRepository();
        using var writer = new PlcResultWriter(plc, address);
        using var workflow = new InspectionWorkflow(
            new FakeEngine(), repo, NullLogger<InspectionWorkflow>.Instance, writer);

        await workflow.AcceptExternalAsync(OkReport(), CancellationToken.None);
        await workflow.AcceptExternalAsync(NgReport(), CancellationToken.None);

        await WaitUntilAsync(() => plc.Writes().Count >= 2,
            "两帧都判完了，PLC 侧却一条回写都没收到 —— 后台消费线程没跑起来");

        var writes = plc.Writes();
        var codes = writes.Select(w => w.Value).ToList();

        Assert.Contains(PlcResultWriter.OkCode, codes);
        Assert.Contains(PlcResultWriter.NgCode, codes);
        Assert.True(writes.All(w => w.Address == address),
            "回写打到了别的位置：PLC 侧会一直读一个永远不变的寄存器");
    }

    // ───────────────────────── 下面是这组用例的共用脚手架 ─────────────────────────

    /// <summary>本次用例独占的临时目录。用 GUID 而不是固定名字，是为了并行/重复跑时不互相覆盖。</summary>
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs-workflow-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 造一张真 bmp。用 ImWrite 而不是手搓 BitmapSource：被测代码走的是
    /// <c>Cv2.ImRead</c>，只有真的编解码过一遍才算测到了读图那一段。
    /// </summary>
    private static string WriteBmp(string dir, string name, int width = 64, int height = 48)
    {
        var path = Path.Combine(dir, name);
        using var mat = new Mat(height, width, MatType.CV_8UC3, new Scalar(30, 30, 30));
        Assert.True(Cv2.ImWrite(path, mat), $"测试图没写出来：{path}");
        return path;
    }

    /// <summary>一帧"平台判 OK"的报文。带一项正常结果，避免被当成空结果集判 NG。</summary>
    private static InspectionReport OkReport(string imagePath = "")
    {
        var report = new InspectionReport
        {
            Timestamp = DateTime.Now,
            TriggerSource = "VM",
            Engine = EngineNames.Vm,
            ImagePath = imagePath
        };
        report.SetToolResult("外径", ToolResult.Ok("外径 12.68mm，偏差 0.02mm"));
        return report;
    }

    /// <summary>一帧"平台判 NG"的报文。结论来自 VM，工作流一个字都不该改。</summary>
    private static InspectionReport NgReport()
    {
        var report = new InspectionReport
        {
            Timestamp = DateTime.Now,
            TriggerSource = "VM",
            Engine = EngineNames.Vm,
            ImagePath = string.Empty
        };
        report.SetToolResult("外径", ToolResult.Ng("外径 12.95mm，超上限 0.25mm"));
        return report;
    }

    /// <summary>位图的原始像素。两张图尺寸相同时才能逐字节比。</summary>
    private static byte[] Pixels(BitmapSource bitmap)
    {
        var stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
        var buffer = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(buffer, stride, 0);
        return buffer;
    }

    /// <summary>
    /// 轮询等待，最多 2 秒。
    ///
    /// 不写死 <c>Task.Delay(500)</c>：PLC 回写是后台线程消费队列，慢机器上 500ms 可能不够、
    /// 快机器上又白等；死等还会让"线程根本没起来"这个故障表现成"偶发失败"。
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string message, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.True(condition(), $"{message}（等了 {timeoutMs}ms）");
    }

    /// <summary>
    /// PLC 连接替身：只记 <c>WriteShort</c> 收到了什么，不碰任何真实设备。
    ///
    /// 必须加锁：写入发生在 <see cref="PlcResultWriter"/> 的后台消费线程，
    /// 读取发生在测试线程 —— 用普通 List 会偶发地在枚举时撞上"集合已修改"。
    /// </summary>
    private sealed class RecordingPlc : IPlcConnection
    {
        private readonly List<(PlcAddress Address, short Value)> _writes = new();
        private readonly object _sync = new();

        public bool IsConnected => true;

        public string? LastError => null;

        public bool Connect() => true;

        public void Disconnect() { }

        public short? ReadShort(PlcAddress address) => 0;

        public bool? ReadBit(PlcAddress address) => false;

        public bool WriteShort(PlcAddress address, short value)
        {
            lock (_sync) _writes.Add((address, value));
            return true;
        }

        public bool WriteBit(PlcAddress address, bool value) => true;

        /// <summary>到目前为止收到的回写（快照）</summary>
        public List<(PlcAddress Address, short Value)> Writes()
        {
            lock (_sync) return _writes.ToList();
        }
    }
}
