using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionStation.App.Services.Imaging;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;
using VisionStation.Devices;
using VisionStation.Storage;

namespace VisionStation.App.Services.Inspection;

/// <summary>
/// 检测链路本体：采图 → 检测 → 标注 → 存图 → 存库。
/// 从 <c>MainViewModel</c> 搬出来（S4a），除了日志不碰任何界面对象。
/// </summary>
public sealed class InspectionWorkflow : IInspectionWorkflow, IDisposable
{
    private readonly ICamera _camera;
    private readonly Pipeline _pipeline;
    private readonly IInspectionRepository _repo;
    private readonly ImageStoreService _imageStore;
    private readonly CalibrationConfig _calib;
    private readonly RecipeConfig _recipe;
    private readonly ILogger<InspectionWorkflow> _logger;

    /// <summary>
    /// 当前 ROI 列表。整体替换而不是原地改：后台线程在跑帧时读的是自己那份快照，
    /// 编辑器保存时只换引用，两边不会同时写同一个 List。
    /// </summary>
    private List<RoiRegion> _rois;

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

    public InspectionWorkflow(
        ICamera camera,
        Pipeline pipeline,
        IInspectionRepository repo,
        ImageStoreService imageStore,
        CalibrationConfig calib,
        RecipeConfig recipe,
        ILogger<InspectionWorkflow> logger)
    {
        _camera = camera;
        _pipeline = pipeline;
        _repo = repo;
        _imageStore = imageStore;
        _calib = calib;
        _recipe = recipe;
        _logger = logger;
        _rois = ReadRoisFromRecipe();

        // ROI 编辑器保存后重新读配方
        WeakReferenceMessenger.Default.Register<InspectionWorkflow, RoisUpdatedMessage>(this,
            (wf, _) =>
            {
                wf._rois = wf.ReadRoisFromRecipe();
                wf._logger.LogInformation("ROIs 已重新加载，当前 {Count} 个区域", wf._rois.Count);
            });
    }

    private List<RoiRegion> ReadRoisFromRecipe() => _recipe.Rois.Select(d => new RoiRegion
    {
        Name = d.Name,
        X = d.X,
        Y = d.Y,
        Width = d.Width,
        Height = d.Height
    }).ToList();

    public Task<InspectionRunResult?> RunOnceAsync(CancellationToken ct)
    {
        // 整段丢到线程池：采图（SDK 阻塞）、Pipeline（CPU 密集）、存图（文件 IO）、
        // 存库（EF 同步段）都没有让出点，留在调用线程上 await 一样会卡死 UI。
        // 传 CancellationToken.None 给 Task.Run：取消由方法内部处理，
        // 避免"任务还没开始就被取消"和"跑到一半被取消"两种语义混在一起。
        return Task.Run(() => RunCoreAsync(ct), CancellationToken.None);
    }

    private async Task<InspectionRunResult?> RunCoreAsync(CancellationToken ct)
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

            // ROI 取一次快照：编辑器可能在下一行之后换掉引用
            var rois = _rois;

            _logger.LogInformation("[采图尺寸] 原始图像: {W}x{H} px, 通道数:{C}, 文件:{F}",
                frame.Image.Width, frame.Image.Height, frame.Image.Channels(),
                Path.GetFileName(frame.FilePath));

            // 配方与画面尺寸不匹配 = 这帧的所有结论都不可信（ROI 框在零件外、标定比例算错），
            // 必须拒绝，不能"照跑不误然后报 NG"——那会让现场以为是产品不良而不是配置错了。
            var mismatch = _recipe.CheckSensor(frame.Image.Width, frame.Image.Height);
            if (mismatch is not null)
            {
                ReportSensorMismatchOnce(mismatch);
                return null;
            }

            var original = MatBitmap.ToBitmapSource(frame.Image);

            var report = _pipeline.Run(frame.Image, rois, _calib, "Manual", frame.FilePath);

            using var annotatedMat = frame.Image.Clone();
            DrawAnnotations(report, annotatedMat);
            var annotated = MatBitmap.ToBitmapSource(annotatedMat);

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

            _logger.LogInformation("判定:{V} 文件:{F} 耗时:{Ms}ms",
                report.OverallVerdict, Path.GetFileName(frame.FilePath), sw.ElapsedMilliseconds);

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

    /// <summary>在图上叠加 ROI 框、圆、缺陷轮廓、判定水印。</summary>
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
