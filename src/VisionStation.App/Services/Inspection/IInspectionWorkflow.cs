using System.Windows.Media.Imaging;
using VisionStation.Core.Models;

namespace VisionStation.App.Services.Inspection;

/// <summary>
/// 一次检测的编排：采图 → Pipeline → 标注 → 存图 → 存库，返回一份可显示的结果。
///
/// 为什么它不在 ViewModel 里：这段代码里没有一行与界面有关，
/// 但它决定了界面会不会卡。放在 VM 里的后果是"线程归属"和"界面逻辑"
/// 绑死在一起，测的时候只能整个窗口一起跑。
///
/// 它不是 CD 里那套"为可测而生的服务"：
///   - 真实现只有 <see cref="InspectionWorkflow"/>；
///   - 测试里需要它的第二个实现，是因为"两个触发并发只跑一帧""关闭时等在途"
///     必须能控制"这一帧什么时候返回"，用真实现＋真 Pipeline 做不到（Pipeline 没有可阻塞的注入口）。
/// </summary>
public interface IInspectionWorkflow
{
    /// <summary>
    /// 跑一帧。返回 null 表示这一帧没有可显示的结果（采图失败或被取消）。
    /// </summary>
    /// <remarks>
    /// 实现约定（调用方可以依赖）：
    ///   1. 整段在后台线程执行，返回的位图已 <c>Freeze()</c>，可直接跨线程绑定；
    ///   2. 不访问 Dispatcher / 不更新任何 ViewModel / 不发消息；
    ///   3. 帧图像的 <c>Mat</c> 在方法内释放，调用方拿到的只有托管位图。
    /// </remarks>
    Task<InspectionRunResult?> RunOnceAsync(CancellationToken ct);

    /// <summary>
    /// 外部引擎（VisionMaster）推来一帧结果：读图 → 标注 → 存库，返回可显示的结果。
    ///
    /// 为什么不复用 <see cref="RunOnceAsync"/>：那一条是"程序主动采图 → 检测"，
    /// VM 模式下方向和数据源都不一样（图在 VM 侧、结果已是最终的）。
    /// 硬塞进一个方法会让里面到处是 if，两种模式都变得看不清。
    /// </summary>
    /// <param name="report">已由 VM 结果解析出的报告（判定不再修改）</param>
    /// <param name="ct">取消令牌</param>
    Task<InspectionRunResult?> AcceptExternalAsync(InspectionReport report, CancellationToken ct);
}

/// <summary>
/// 一帧的结果。工作流只陈述事实，怎么显示由界面层决定。
/// </summary>
/// <param name="Report">检测报告（判定、各工具结果、标注指令）</param>
/// <param name="OriginalImage">原始图，已冻结</param>
/// <param name="AnnotatedImage">叠加标注后的图，已冻结</param>
/// <param name="CycleMs">本帧耗时（采图到存库完成）</param>
/// <param name="ImageSaved">图片是否真的写到了磁盘</param>
/// <param name="ImageError">存图失败原因；成功或跳过时为 null</param>
/// <param name="ConsecutiveImageFailures">连续存图失败次数（成功即归零）</param>
/// <param name="CameraOnline">本帧结束后相机的可用状态</param>
public sealed record InspectionRunResult(
    InspectionReport Report,
    BitmapSource? OriginalImage,
    BitmapSource? AnnotatedImage,
    long CycleMs,
    bool ImageSaved,
    string? ImageError,
    int ConsecutiveImageFailures,
    bool CameraOnline);
