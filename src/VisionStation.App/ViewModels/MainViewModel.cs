using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionStation.App.Composition.Lifecycle;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;
using VisionStation.Devices;
using VisionStation.Storage;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 主流程编排器
///
/// 职责（只做这一件事）：
///   1. 监听 ITriggerSource.Triggered 事件
///   2. 收到触发 → Camera.Capture() 采图
///   3. Pipeline.Run() 跑检测
///   4. 保存结果到数据库和文件
///   5. 发 InspectionResultMessage → 各面板自动更新
///
///
/// 依赖：
///   - ITriggerSource：接收触发信号
///   - ICamera：采集图像
///   - Pipeline：执行检测算法
///   - InspectionRepository：存数据库
///   - ImageStoreService：存图片文件
    ///   - ImagePanelVM：直接赋值图像（像素转换在此，属于UI层职责）
    ///   - StatusBarVM：直接推送 FPS / Cycle / OK / NG（性能考虑，不走消息）
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    // ── 依赖注入的底层组件 ──
    private readonly ITriggerSource _trigger;
    private readonly ICamera _camera;
    private readonly Pipeline _pipeline;
    private readonly IInspectionRepository _repo;
    private readonly ImageStoreService _imageStore;
    private readonly CalibrationConfig _calib;
    private readonly ILogger<MainViewModel> _logger;
    private readonly ImagePanelViewModel _imagePanel;
    private readonly RecipeConfig _recipe;
    private readonly StatusBarViewModel _statusBar;

    // S2.3（关闭顺序）依赖：关闭开始后拒绝新触发、并让关闭流程能看到"还有没有一帧在跑"。
    // 骨架期只建立依赖，拦截与租约在实现阶段接入（见 docs/S2.3-关闭顺序-设计.md）。
    private readonly ShutdownState _shutdown;
    private readonly InFlightWorkMonitor _inFlight;

    // ── 检测区域：来自配方文件 ──
    private List<RoiRegion> _rois;

    // ── 运行时状态 ──
    private bool _isProcessing;          // 防并发：上一帧没处理完，新触发直接跳过
    private int _totalOk, _totalNg;      // 累计统计
    private double _emaCycleMs;          // 单帧耗时指数滑动平均（FPS 平滑，抗抖动）

    /// <summary>
    /// 连续存图失败次数。成功一次即清零。
    ///
    /// 为什么需要它？
    ///   单次存图失败可以容忍（偶发 IO 抖动），但**连续失败意味着存储子系统已经不可用**：
    ///   磁盘满、目录被删、权限变化。这时如果只逐帧记一行日志，
    ///   产线会以 5 帧/秒的速度安静地丢掉全部 NG 图，事后一件都查不到。
    ///   连续计数是把"偶发"和"系统性故障"区分开的唯一手段。
    /// </summary>
    private int _imageSaveFailures;
    private const int ImageSaveFailureAlertThreshold = 10;

    // ═══════════════════════════════════════════════════════════
    //  构造函数 —— DI 注入所有依赖，连线触发信号
    // ═══════════════════════════════════════════════════════════
    public MainViewModel(
        ITriggerSource trigger, ICamera camera, Pipeline pipeline,
        IInspectionRepository repo, ImageStoreService imageStore,
        CalibrationConfig calib, ILogger<MainViewModel> logger,
        ImagePanelViewModel imagePanel, StatusBarViewModel statusBar, RecipeConfig recipe,
        ShutdownState shutdown, InFlightWorkMonitor inFlight)
    {
        _trigger = trigger; _camera = camera; _pipeline = pipeline;
        _repo = repo; _imageStore = imageStore;
        _calib = calib; _logger = logger;
        _imagePanel = imagePanel;
        _statusBar = statusBar;
        _recipe = recipe;
        _shutdown = shutdown;
        _inFlight = inFlight;

        // 从配方文件取 ROI
        _rois = recipe.Rois.Select(d => new RoiRegion
        {
            Name = d.Name, X = d.X, Y = d.Y,
            Width = d.Width, Height = d.Height
        }).ToList();

        // ★ 核心连线：触发信号 → OnTriggered → ProcessFrame
        _trigger.Triggered += OnTriggered;

        // 状态栏相机指示必须在启动时就反映真实连接状态。
        // CameraStatusMessage 只在每帧检测后发送，不初始化的话，
        // 相机明明连上了，状态栏也会一直挂着灰点，直到第一次检测跑完为止。
        _statusBar.CameraOnline = _camera.IsAvailable;

        // ROI 编辑器保存 → 重新从配方文件加载
        WeakReferenceMessenger.Default.Register<MainViewModel, RoisUpdatedMessage>(this,
            (vm, _) =>
            {
                vm._rois = vm._recipe.Rois.Select(d => new RoiRegion
                {
                    Name = d.Name, X = d.X, Y = d.Y,
                    Width = d.Width, Height = d.Height
                }).ToList();
                _logger.LogInformation("ROIs 已重新加载，当前 {Count} 个区域", vm._rois.Count);
            });

        // 启动日志
        WeakReferenceMessenger.Default.Send(new LogMessage("VisionStation 已就绪", LogMsgLevel.Success));
        _logger.LogInformation("VisionStation 启动 - 相机:{Cam} 标定:{Px}px/mm",
            _camera.IsAvailable, _calib.PixelPerMillimeter);

    }

    // ═══════════════════════════════════════════════════════════
    //  触发 → 处理（保证 UI 线程安全）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 触发回调入口
    /// 如果是 UI 线程 → 直接处理
    /// 如果是后台线程（TimerTrigger）→ 切到 UI 线程再处理
    /// </summary>
    private async void OnTriggered()
    {
        // 关闭已开始：不再开新的一帧。
        // 这一步必须在碰 Dispatcher 之前 —— 关闭流程可能停在任意线程上，
        // 而此时 Application.Current 未必还可用。
        if (_shutdown.IsStopping)
        {
            _logger.LogDebug("关闭已开始，忽略本次触发");
            return;
        }

        if (Application.Current.Dispatcher.CheckAccess())
            await ProcessFrame();
        else
            await Application.Current.Dispatcher.InvokeAsync(ProcessFrame);
    }

    /// <summary>
    /// 完整的一帧处理流程（采图 → 检测 → 标注 → 保存 → 发消息）
    /// </summary>
    private async Task ProcessFrame()
    {
        // 防并发：上一帧没跑完，这一帧直接跳过
        if (_isProcessing) return;
        _isProcessing = true;

        // 在途登记（C3）：关闭流程靠这个计数回答"还有没有一帧在跑"，而不是 sleep 一段再猜。
        // Begin 只做锁内自增、不会抛；租约由 using 在所有退出路径（含中途 return/异常）归还。
        using var lease = _inFlight.Begin();

        var sw = Stopwatch.StartNew();

        try
        {
            // ── 1. 采图 ──
            var frame = _camera.Capture();
            if (frame == null)
            {
                _logger.LogWarning("采图失败 - 无图像");
                WeakReferenceMessenger.Default.Send(new LogMessage("无图像", LogMsgLevel.Warning));
                return;
            }

            // ── 2. 记录采图尺寸 ──
            _logger.LogInformation("[采图尺寸] 原始图像: {W}x{H} px, 通道数:{C}, 文件:{F}",
                frame.Image.Width, frame.Image.Height, frame.Image.Channels(),
                Path.GetFileName(frame.FilePath));

            // ── 3. Mat → WPF BitmapSource（UI 类型转换，放在 ViewModel 层因为涉及 OpenCV）──
            _imagePanel.OriginalImage = MatToBitmap(frame.Image);

            // ── 4. 运行检测流水线 ──
            var report = _pipeline.Run(frame.Image, _rois, _calib,
                "Manual", frame.FilePath);

            // ── 4. 画标注叠加图 ──
            using var anno = frame.Image.Clone();
            DrawAnnotations(report, anno);
            _imagePanel.AnnotatedImage = MatToBitmap(anno);
            _imagePanel.SelectedTab = _imagePanel.Tabs[1]; // 自动切到"检测"tab

            // ── 5. 持久化：图片文件 + 数据库 ──
            // 存图结果必须检查，不能像以前那样直接丢弃返回值：
            // Cv2.ImWrite 写失败只返回 false 不抛异常，不看结果就会变成
            // "系统认为存了，实际磁盘上没有" —— NG 图在无人知晓的情况下永久丢失。
            var imageResult = _imageStore.Save(frame, report);
            if (imageResult.IsFailed)
            {
                _imageSaveFailures++;
                _logger.LogError("[存图失败] 连续 {N} 次：{Err}", _imageSaveFailures, imageResult.Error);
                WeakReferenceMessenger.Default.Send(new LogMessage(
                    $"存图失败（连续 {_imageSaveFailures} 次）: {imageResult.Error}", LogMsgLevel.Warning));

                if (_imageSaveFailures == ImageSaveFailureAlertThreshold)
                {
                    // 升级为显式告警：这里已经不能叫"偶发失败"，是存储不可用
                    WeakReferenceMessenger.Default.Send(new LogMessage(
                        $"存图已连续失败 {ImageSaveFailureAlertThreshold} 次，图片追溯中断，请检查磁盘空间与写权限",
                        LogMsgLevel.Error));
                    _logger.LogError("存图连续失败 {N} 次，存储子系统不可用", _imageSaveFailures);
                }
            }
            else if (imageResult.IsSaved)
            {
                _imageSaveFailures = 0;   // 恢复即清零
            }

            await _repo.SaveAsync(report);

            // ── 6. 累计统计 ──
            if (report.IsOk) _totalOk++; else _totalNg++;

            // ── 6.1 状态栏实时指标：OK/NG 累计 + 单帧耗时 + EMA 平滑 FPS ──
            _statusBar.OkCount = _totalOk;
            _statusBar.NgCount = _totalNg;
            var cycleMs = sw.ElapsedMilliseconds;
            _statusBar.CycleTime = $"{cycleMs} ms";
            _emaCycleMs = _emaCycleMs == 0 ? cycleMs : _emaCycleMs * 0.7 + cycleMs * 0.3;
            _statusBar.Fps = $"{1000.0 / _emaCycleMs:0.0}";

            // ── 7. 发消息 → 订阅者各自更新 ──
            WeakReferenceMessenger.Default.Send(new InspectionResultMessage(report));
            WeakReferenceMessenger.Default.Send(new CameraStatusMessage(_camera.IsAvailable));

            _logger.LogInformation("判定:{V} 文件:{F} 耗时:{Ms}ms",
                report.OverallVerdict, Path.GetFileName(frame.FilePath), sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检测异常");
            WeakReferenceMessenger.Default.Send(new LogMessage($"异常: {ex.Message}", LogMsgLevel.Error));
        }
        finally
        {
            _isProcessing = false;
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  图像标注 —— 在原始图上叠加 ROI 框、圆、缺陷轮廓、判定水印
    // ═══════════════════════════════════════════════════════════
    private void DrawAnnotations(InspectionReport r, Mat img)
    {
        // ── 遍历所有 Tool 的 DrawCommand，统一渲染 ──
        foreach (var (toolName, result) in r.Results)
        {
            foreach (var cmd in result.Overlays)
            {
                switch (cmd)
                {
                    case DrawRect rect:
                        Cv2.Rectangle(img, rect.Rect, rect.Color, rect.Thickness);
                        if (rect.Label != null)
                            Cv2.PutText(img, rect.Label,
                                new OpenCvSharp.Point(rect.Rect.X, rect.Rect.Y - 5),
                                HersheyFonts.HersheySimplex, 0.5, rect.Color, 2);
                        break;

                    case DrawCircle circle:
                        Cv2.Circle(img, circle.Center, (int)circle.Radius,
                            circle.Color, circle.Thickness);
                        if (circle.Label != null)
                            Cv2.PutText(img, circle.Label,
                                new OpenCvSharp.Point(circle.Center.X - 20, circle.Center.Y - circle.Radius - 5),
                                HersheyFonts.HersheySimplex, 0.5, circle.Color, 2);
                        break;

                    case DrawContour contour:
                        Cv2.DrawContours(img, new[] { contour.Points }, -1,
                            contour.Color, contour.Thickness);
                        break;
                }
            }
        }

        // 左上角 OK/NG 水印
        Cv2.PutText(img, r.OverallVerdict,
            new OpenCvSharp.Point(20, 50),
            HersheyFonts.HersheyDuplex, 1.5,
            r.IsOk ? new Scalar(0, 180, 0) : new Scalar(0, 0, 220), 3);
    }

    // ═══════════════════════════════════════════════════════════
    //  OpenCV Mat → WPF BitmapSource
    //  属于 UI 适配层职责，放在这里是因为 ViewModel 层需要用到
    // ═══════════════════════════════════════════════════════════
    private static BitmapSource MatToBitmap(Mat mat)
    {
        if (mat == null || mat.Empty()) return null!;

        // 统一转 BGR 三通道
        Mat d;
        if (mat.Channels() == 1)
        {
            d = new Mat();
            Cv2.CvtColor(mat, d, ColorConversionCodes.GRAY2BGR);
        }
        else if (mat.Channels() == 4)
        {
            d = new Mat();
            Cv2.CvtColor(mat, d, ColorConversionCodes.BGRA2BGR);
        }
        else d = mat.Clone();

        // 先保存宽高，再释放 Mat（防止 AccessViolation）
        int w = d.Cols, h = d.Rows, stride = w * 3;
        var data = new byte[stride * h];
        System.Runtime.InteropServices.Marshal.Copy(d.Data, data, 0, data.Length);
        d.Dispose();

        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, data, stride);
    }

    /// <summary>
    /// 释放资源：取消触发订阅 + 停止触发源
    /// </summary>
    public void Dispose()
    {
        _trigger.Triggered -= OnTriggered;
        _trigger.Stop();
    }
}
