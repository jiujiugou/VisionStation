using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionStation.App.Services.Imaging;
using VisionStation.Core.Engines;
using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Storage;

namespace VisionStation.App.Services.Inspection;

/// <summary>
/// 检测链路本体：采图 → 检测 → 标注 → 存图 → 存库。
///
/// 与改造前的区别只有一处，但很关键：**它不再知道检测是怎么做的**。
/// 采图归 ICamera，判定归 <see cref="IInspectionEngine"/>（VM 流程或 ONNX），
/// 本类只负责把三者串起来，并保证"每一帧的失败都能被看见"。
/// </summary>
public sealed class InspectionWorkflow : IInspectionWorkflow, IDisposable
{
    private readonly ICamera _camera;
    private readonly IInspectionEngine _engine;
    private readonly IInspectionRepository _repo;
    private readonly ImageStoreService _imageStore;
    private readonly RecipeConfig _recipe;
    private readonly ILogger<InspectionWorkflow> _logger;

    /// <summary>
    /// 连续存图失败次数。成功一次即清零。
    ///
    /// 单次失败可以容忍（偶发 IO 抖动），连续失败说明存储子系统已经不可用：
    /// 磁盘满、目录被删、权限变化。只逐帧记一行日志的话，
    /// 产线会以 5 帧/秒的速度安静地丢掉全部 NG 图，事后一件都查不到。
    /// </summary>
    private int _imageSaveFailures;

    /// <summary>
    /// 配方与画面尺寸不匹配是否已经报过。
    /// 自动模式 2 秒一帧，每帧都记 ERROR 会把日志刷成一片，真正的问题反而看不见 ——
    /// 只在第一次大声说，后续降到 Debug。
    /// </summary>
    private int _sensorMismatchReported;

    /// <summary>
    /// 上屏最大宽度。显示区不到 900px，按 2448 全尺寸转换等于每张白拷 15MB。
    /// 1280 是折中：在 1080p 屏上放大看仍然清楚，转换开销只有全尺寸的 27%。
    /// **标注仍画在原尺寸上**（坐标是绝对像素），画完再缩。
    /// </summary>
    private const int DisplayMaxWidth = 1280;

    public InspectionWorkflow(
        ICamera camera,
        IInspectionEngine engine,
        IInspectionRepository repo,
        ImageStoreService imageStore,
        RecipeConfig recipe,
        ILogger<InspectionWorkflow> logger)
    {
        _camera = camera;
        _engine = engine;
        _repo = repo;
        _imageStore = imageStore;
        _recipe = recipe;
        _logger = logger;
    }

    /// <summary>当前使用的引擎名（状态栏/日志用）</summary>
    public string EngineName => _engine.Name;

    public Task<InspectionRunResult?> RunOnceAsync(TriggerOrigin origin, CancellationToken ct)
    {
        // 推驱动引擎（VM 模式）不响应主动检测。自动循环照旧会按节拍调到这里，
        // 必须**当场返回 null 且不入库** —— 否则每 2 秒一条"VM 模式不支持主动检测"的假 NG，
        // 良率统计会被直接污染。
        if (_engine is IPushDrivenEngine)
        {
            _logger.LogDebug("[引擎] {Engine} 由外部推送驱动，跳过本次主动检测", _engine.Name);
            return Task.FromResult<InspectionRunResult?>(null);
        }

        // 整段丢到线程池：采图（SDK 阻塞）、检测（CPU 密集）、存图（文件 IO）、
        // 存库（EF 同步段）都没有让出点，留在调用线程上 await 一样会卡死 UI。
        // 传 CancellationToken.None 给 Task.Run：取消由方法内部处理，
        // 避免"任务还没开始就被取消"和"跑到一半被取消"两种语义混在一起。
        return Task.Run(() => RunCoreAsync(origin, ct), CancellationToken.None);
    }

    private async Task<InspectionRunResult?> RunCoreAsync(TriggerOrigin origin, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        CameraFrame? frame = null;

        try
        {
            ct.ThrowIfCancellationRequested();

            var frameImage = _camera.Capture();
            if (frameImage is null)
            {
                _logger.LogWarning("采图失败 - 无图像");
                return null;
            }

            frame = frameImage;

            _logger.LogInformation("[采图尺寸] 原始图像: {W}x{H} px, 通道数:{C}, 文件:{F}",
                frame.Image.Width, frame.Image.Height, frame.Image.Channels(),
                Path.GetFileName(frame.FilePath));

            // 配方与画面尺寸不匹配 = 这帧的所有结论都不可信（流程里的 ROI 在零件外、标定比例算错），
            // 必须拒绝，不能"照跑不误然后报 NG"——那会让现场以为是产品不良而不是配置错了。
            var mismatch = _recipe.CheckSensor(frame.Image.Width, frame.Image.Height);
            if (mismatch is not null)
            {
                ReportSensorMismatchOnce(mismatch);
                return null;
            }

            var original = MatBitmap.ToBitmapSource(frame.Image, DisplayMaxWidth);

            var report = _engine.Inspect(new InspectionRequest
            {
                Image = frame.Image,
                // 来源由触发那一刻决定，不再写死。写死 Manual 的后果是：
                // 定时跑了一整班的记录，历史页上全变成"人点出来的"，追责时说不清。
                TriggerSource = origin.ToReportValue(),
                ImagePath = frame.FilePath
            });

            // 引擎把失败也表达成 NG 报告（而不是抛异常），所以"报告里没有可判定的项"
            // 就等于"这一件没检出来"。必须显式说出来，不能让它无声无息地进库。
            if (report.Results.Count == 0)
                _logger.LogError("[引擎] {Engine} 未给出任何结论，本帧按 NG 处理", _engine.Name);
            else if (report.Results.TryGetValue(ResultKeys.Engine, out var engineFailure))
                _logger.LogError("[引擎] {Engine}: {Reason}", _engine.Name, engineFailure.Summary);

            using var annotatedMat = frame.Image.Clone();
            DrawAnnotations(report, annotatedMat);
            var annotated = MatBitmap.ToBitmapSource(annotatedMat, DisplayMaxWidth);

            ct.ThrowIfCancellationRequested();

            // 存图结果必须检查：Cv2.ImWrite 写失败只返回 false 不抛异常，
            // 不看结果就会变成"系统认为存了，磁盘上其实没有"。
            var imageResult = _imageStore.Save(frame, report);
            if (imageResult.IsFailed)
            {
                _imageSaveFailures++;
                _logger.LogError("[存图失败] 连续 {N} 次：{Err}", _imageSaveFailures, imageResult.Error);
            }
            else if (imageResult.IsSaved)
            {
                _imageSaveFailures = 0;   // 恢复即清零
            }

            await _repo.SaveAsync(report).ConfigureAwait(false);

            _logger.LogInformation("判定:{V} 引擎:{E} 文件:{F} 耗时:{Ms}ms",
                report.OverallVerdict, report.Engine,
                Path.GetFileName(frame.FilePath), sw.ElapsedMilliseconds);

            return new InspectionRunResult(
                report,
                original,
                annotated,
                sw.ElapsedMilliseconds,
                imageResult.IsSaved,
                imageResult.Error,
                _imageSaveFailures,
                _camera.IsAvailable);
        }
        finally
        {
            // 帧的 Mat 归工作流所有：谁创建谁释放。
            // 以前这里没人释放，靠 Mat 的终结器兜底 —— 12MP 彩色帧一帧 36MB，
            // 5 帧/秒就是每分钟数百 MB 的非托管分配压力，GC 根本跟不上。
            frame?.Image.Dispose();
        }
    }

    /// <summary>
    /// VM 推来一帧结果：把图读出来做显示与标注，结论原样入库。
    ///
    /// 这里**不存图**：VM 已经把图存在自己的输出目录了，上位机再存一份就是两份真相，
    /// 而且会白白吃掉磁盘。报告里的 <c>ImagePath</c> 直接指向 VM 那一边的文件。
    ///
    /// **不重新判定**：报告里的 OK/NG 全部来自 VM，本方法一个字都不改。
    /// </summary>
    public Task<InspectionRunResult?> AcceptExternalAsync(InspectionReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        return Task.Run(() => AcceptExternalCoreAsync(report, ct), CancellationToken.None);
    }

    private async Task<InspectionRunResult?> AcceptExternalCoreAsync(InspectionReport report, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        ct.ThrowIfCancellationRequested();

        BitmapSource? original = null;
        BitmapSource? annotated = null;
        var imageAvailable = false;

        // 没图时必须说清是哪一种"没图"：四种情况在界面上都只是黑屏，
        // 但处理方式完全不同 —— 等、去 VM 里配、查路径、查权限。
        string? imageReason = null;

        var path = report.ImagePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            imageReason = "本帧结果没有带图像路径（VM 侧未发 imagePath）";
        }
        else if (!File.Exists(path))
        {
            imageReason = $"VM 给的图像路径不存在：{path}";
            _logger.LogWarning("[VM] VM 报告里的图像路径不存在：{Path}", path);
        }
        else
        {
            try
            {
                using var mat = Cv2.ImRead(path, ImreadModes.Color);
                if (mat.Empty())
                {
                    imageReason = $"图像无法解码（空文件或格式不支持）：{path}";
                    _logger.LogWarning("[VM] 图像无法解码：{Path}", path);
                }
                else
                {
                    original = MatBitmap.ToBitmapSource(mat, DisplayMaxWidth);

                    using var overlay = mat.Clone();
                    DrawAnnotations(report, overlay);
                    annotated = MatBitmap.ToBitmapSource(overlay, DisplayMaxWidth);

                    imageAvailable = true;
                }
            }
            catch (Exception ex) when (ex is IOException or OpenCVException or UnauthorizedAccessException)
            {
                // 图画不出来不该影响判定入库 —— 结论已经定了，少一张图是次要问题
                imageReason = $"读取图像失败：{ex.Message}";
                _logger.LogWarning(ex, "[VM] 读取 VM 图像失败：{Path}", path);
            }
        }

        ct.ThrowIfCancellationRequested();

        await _repo.SaveAsync(report).ConfigureAwait(false);

        if (report.Results.Count == 0)
            _logger.LogError("[VM] 未给出任何结论，本帧按 NG 处理");
        else if (report.Results.TryGetValue(ResultKeys.Engine, out var engineFailure))
            _logger.LogError("[VM] {Reason}", engineFailure.Summary);

        _logger.LogInformation("判定:{V} 引擎:{E} 来源:VM推送 耗时:{Ms}ms",
            report.OverallVerdict, report.Engine, sw.ElapsedMilliseconds);

        // ConsecutiveImageFailures 恒为 0：VM 模式不存图，没有"连续存图失败"这回事。
        // CameraOnline 表示 VM 链路是否在跑 —— 界面上的相机状态位复用它。
        return new InspectionRunResult(
            report,
            original,
            annotated,
            sw.ElapsedMilliseconds,
            imageAvailable,
            null,
            0,
            true,
            imageReason);
    }

    /// <summary>
    /// 在图上叠加引擎给出的标注与判定水印。
    ///
    /// 引擎只声明"画什么"（DrawRect / DrawCircle / DrawContour），
    /// 具体怎么画留在这里 —— 换引擎时标注不用重写。
    /// </summary>
    private static void DrawAnnotations(InspectionReport report, Mat img)
    {
        foreach (var (_, result) in report.Results)
        {
            foreach (var cmd in result.Overlays)
            {
                switch (cmd)
                {
                    case DrawRect rect:
                        Cv2.Rectangle(img, rect.Rect, rect.Color, rect.Thickness);
                        if (rect.Label != null)
                        {
                            Cv2.PutText(img, rect.Label,
                                new OpenCvSharp.Point(rect.Rect.X, rect.Rect.Y - 5),
                                HersheyFonts.HersheySimplex, 0.5, rect.Color, 2);
                        }

                        break;

                    case DrawCircle circle:
                        Cv2.Circle(img, circle.Center, (int)circle.Radius,
                            circle.Color, circle.Thickness);
                        if (circle.Label != null)
                        {
                            Cv2.PutText(img, circle.Label,
                                new OpenCvSharp.Point(circle.Center.X - 20,
                                    circle.Center.Y - circle.Radius - 5),
                                HersheyFonts.HersheySimplex, 0.5, circle.Color, 2);
                        }

                        break;

                    case DrawContour contour:
                        Cv2.DrawContours(img, new[] { contour.Points }, -1,
                            contour.Color, contour.Thickness);
                        break;
                }
            }
        }

        // 左上角 OK/NG 水印
        Cv2.PutText(img, report.OverallVerdict,
            new OpenCvSharp.Point(20, 50),
            HersheyFonts.HersheyDuplex, 1.5,
            report.IsOk ? new Scalar(0, 180, 0) : new Scalar(0, 0, 220), 3);
    }

    public void Dispose()
        => WeakReferenceMessenger.Default.UnregisterAll(this);

    /// <summary>
    /// 配方尺寸不匹配：第一次记 ERROR 并推到界面日志面板，之后只记 Debug。
    /// 光写 ERROR 不够 —— 操作员看的是界面，不是日志文件。
    /// </summary>
    private void ReportSensorMismatchOnce(string reason)
    {
        if (Interlocked.Exchange(ref _sensorMismatchReported, 1) == 1)
        {
            _logger.LogDebug("[配方] {Reason}", reason);
            return;
        }

        _logger.LogError("[配方] {Reason}", reason);
        WeakReferenceMessenger.Default.Send(new LogMessage(reason, LogMsgLevel.Error));
    }
}
