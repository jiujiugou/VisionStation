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
/// 与改造前的区别只有一处，但很关键：**它不再知道检测是怎么做的，也不采图**。
/// 判定归 <see cref="IInspectionEngine"/>，图像由外部平台推送（VM 存盘、报文带路径），
/// 本类只负责"解析图 → 标注 → 入库 → 回写 PLC"，并保证每一帧的失败都能被看见。
/// </summary>
public sealed class InspectionWorkflow : IInspectionWorkflow, IDisposable
{
    /// <summary>引擎。本类只用它的 <c>Name</c> 和"是不是推驱动"两个信息，从不调它去检测。</summary>
    private readonly IInspectionEngine _engine;

    /// <summary>仓储。每帧保存一次，失败会抛（由调用方决定记日志还是中断）。</summary>
    private readonly IInspectionRepository _repo;

    private readonly ILogger<InspectionWorkflow> _logger;

    /// <summary>
    /// PLC 结果回写（可空：未启用 PLC 时为 null）。
    ///
    /// 归属说明：回写**必须在这一层**，不能只在界面上那一层。
    /// 本类有两条判定路径——主动检测（RunCoreAsync）和 VM 推送（AcceptExternalCoreAsync）——
    /// 而 VM 模式走的正是第二条。回写放在 ViewModel 里时只有第一条会回写，
    /// 结果就是"真上产线、VM 判完、PLC 永远收不到 OK/NG"。
    /// </summary>
    private readonly PlcResultWriter? _plcWriter;

    /// <summary>
    /// 图像来源设置（可空：未注册时按"只用报文路径"处理）。
    ///
    /// 归属说明：路径解析放在这里而不是界面 —— 解码、标注、入库都发生在本类，
    /// 界面只拿到"已经解好的位图 + 没图时的原因"。把兜底逻辑放界面，
    /// 会出现"界面显示了图、库里记录的 ImagePath 却是空"的两份真相。
    /// </summary>
    private readonly ImageSourceSettingsStore? _imageSourceSettings;

    /// <summary>
    /// 上屏最大宽度。显示区不到 900px，按 2448 全尺寸转换等于每张白拷 15MB。
    /// 1280 是折中：在 1080p 屏上放大看仍然清楚，转换开销只有全尺寸的 27%。
    /// **标注仍画在原尺寸上**（坐标是绝对像素），画完再缩。
    /// </summary>
    private const int DisplayMaxWidth = 1280;

    /// <summary>
    /// 构造。两个可选依赖（PLC 回写、图像来源设置）用默认参数而不是重载：
    /// 它们缺席是合法状态（没接 PLC / 没配兜底目录），不是配置错误。
    /// </summary>
    /// <param name="engine">引擎（判定已由它给出，本类不改结论）</param>
    /// <param name="repo">检测记录仓储</param>
    /// <param name="logger">日志</param>
    /// <param name="plcWriter">PLC 回写；null = 未启用 PLC</param>
    /// <param name="imageSourceSettings">图像来源设置；null = 只用报文路径</param>
    public InspectionWorkflow(
        IInspectionEngine engine,
        IInspectionRepository repo,
        ILogger<InspectionWorkflow> logger,
        PlcResultWriter? plcWriter = null,
        ImageSourceSettingsStore? imageSourceSettings = null)
    {
        _engine = engine;
        _repo = repo;
        _logger = logger;
        _plcWriter = plcWriter;
        _imageSourceSettings = imageSourceSettings;
    }

    /// <summary>当前使用的引擎名（状态栏/日志用）</summary>
    public string EngineName => _engine.Name;

    /// <summary>
    /// 主动检测 —— 本程序**没有**这条路径了。
    ///
    /// 相机归视觉平台独占，图在平台侧产生，本程序只接收结果。所以这里永远返回 null，
    /// 且**不入库**：自动循环仍会按节拍调进来，若每 2 秒造一条假 NG，良率会被直接污染。
    ///
    /// 保留这个方法是因为 <c>InspectionPageViewModel</c> 的"开始检测"按钮和自动循环
    /// 都挂在同一根轴上；删掉它等于让按钮点下去没反应 —— 那比"明确说不支持"更难排查。
    /// </summary>
    public Task<InspectionRunResult?> RunOnceAsync(TriggerOrigin origin, CancellationToken ct)
    {
        _logger.LogDebug("[引擎] {Engine} 由外部推送驱动，本程序不采图，跳过主动检测", _engine.Name);
        return Task.FromResult<InspectionRunResult?>(null);
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

    /// <summary>
    /// 推送帧的实际处理。**顺序是这张清单的全部内容**：
    ///
    /// <list type="number">
    ///   <item>解析图像路径（报文优先，界面设置兜底）—— 纯计算，不读盘；</item>
    ///   <item>读图、标注、缩放到上屏宽度 —— 失败只记原因，**不影响判定入库**；</item>
    ///   <item>入库 —— 唯一可能抛的一步，抛了由上层记 Error；</item>
    ///   <item>回写 PLC —— 放在入库之后，避免"PLC 放行了库里却没记录"。</item>
    /// </list>
    ///
    /// <para>
    /// 整体跑在线程池上（<see cref="AcceptExternalAsync"/> 用 <c>Task.Run</c> 包起来），
    /// 因为 OpenCV 解码与标注是同步 CPU 活，直接放在接收回调里会卡住 TCP 读取线程。
    /// </para>
    /// </summary>
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

        // 报文里的路径有三种常见形态：空、只是目录、完整文件。翻译成"能读的文件"
        // 的规则统一在 ImageFileResolver 里（报文优先，界面设置的目录兜底）。
        var (path, pathReason) = ImageFileResolver.Resolve(
            report.ImagePath,
            report.Timestamp,
            _imageSourceSettings?.Current ?? ImageSourceSettings.Disabled);

        if (path is null)
        {
            imageReason = pathReason;

            // 带路径却不可用 = 配置或写盘出了问题，值得一条警告；
            // 什么都没带是常态（没配存图），不刷日志。
            if (!string.IsNullOrWhiteSpace(report.ImagePath))
                _logger.LogWarning("[VM] 图像不可用：{Reason}", pathReason);
        }
        else
        {
            // 兜底目录生效时如实记下取的是哪一张：否则事后查"这张图是不是那一件的"没有依据
            if (!string.Equals(path, report.ImagePath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("[VM] 报文路径不可用（{Raw}），改用 {Path}", report.ImagePath, path);

                // 库里的 ImagePath 必须指向**实际显示的那张图**。
                // 只改界面不改记录，等于给现场留了一条"点开历史记录看到的是另一张图"的坑。
                report.ImagePath = path;
            }

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

        // 回写 PLC：VM 推送这条路径以前完全没有回写，导致"判定归 VM、回写没人做"。
        // 与主动检测那条保持一致：入库成功后再写，避免 PLC 放行了库里却没记录。
        _plcWriter?.Write(report.IsOk);

        if (report.Results.Count == 0)
            _logger.LogError("[VM] 未给出任何结论，本帧按 NG 处理");
        else if (report.Results.TryGetValue(ResultKeys.Engine, out var engineFailure))
            _logger.LogError("[VM] {Reason}", engineFailure.Summary);

        _logger.LogInformation("判定:{V} 引擎:{E} 来源:VM推送 耗时:{Ms}ms",
            report.OverallVerdict, report.Engine, sw.ElapsedMilliseconds);

        // ConsecutiveImageFailures 恒为 0：VM 模式不存图，没有"连续存图失败"这回事。
        // ImageLinkOnline 表示平台是否在推 —— 顶栏/状态栏那个"平台"灯复用它。
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

    /// <summary>
    /// 注销消息订阅。
    ///
    /// <para>
    /// 本类目前其实**没有订阅任何消息**（<c>UnregisterAll</c> 是改造前的遗留）。
    /// 保留它是因为容器会在关闭时调 Dispose，删掉这个实现就要跟着改注册方式；
    /// 而且它无害 —— UnregisterAll 对没注册过的对象是空操作。
    /// </para>
    /// </summary>
    public void Dispose()
        => WeakReferenceMessenger.Default.UnregisterAll(this);
}
