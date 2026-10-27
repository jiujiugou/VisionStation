using OpenCvSharp;
using VisionStation.Core.Models;

namespace VisionStation.Core.Engines;

/// <summary>
/// 检测引擎 —— 全系统唯一一条"判定"接缝：上层只认这个接口，不认具体实现。
///
/// <para>
/// <b>定位</b>：它夹在 <c>InspectionWorkflow</c>（消费结论）与具体引擎（产出结论）之间。
/// 运行时只有 <c>VmExternalEngine</c> 一个实现，而它并不真的检测 ——
/// 它占住这个位置是为了让"判定权在 VisionMaster 那边"这件事变成一个类型，
/// 而不是散落在各处 if 里的口头约定。真正的 <see cref="InspectionReport"/>
/// 由 VM 推来的报文经 <c>VmResultParser</c> 翻译出来，不经过 <see cref="Inspect"/>。
/// </para>
///
/// <para>
/// <b>为什么会有这个接口</b>（历史）：曾经确实存在两个实现，
/// <c>Vm</c>（海康流程）与 <c>Onnx</c>（YOLO 兜底），按配方切换是真需求，不是为 mock 造的缝。
/// 2026-10-27 起 ONNX 整条线删除（引擎、离线跑批工具、模型、数据集、相关测试），
/// 于是实现只剩一个 —— 接口留着，是因为"换引擎"的语义还在（配方里有 engine 字段）。
/// </para>
///
/// <para>
/// <b>归属与线程</b>：<c>InspectionEngineFactory</c> 按配方 new 一次，注册为单例，活到进程结束；
/// 被工作流、图像面板、控制面板共享。VM 模式下 <see cref="Inspect"/> 在正常运行期间
/// 一次都不会被调用：自动循环走的是 <c>InspectionWorkflow.RunOnceAsync</c>，
/// 它在相机层删除后已经<b>无条件返回 null</b>；界面上点"开始检测"则由控制面板
/// 直接说明"检测由 VisionMaster 执行"。两条路都不进 <see cref="Inspect"/>。
/// </para>
///
/// <para>
/// <b>边界约定 —— 三条"不"，比接口签名本身更重要</b>：
/// <list type="number">
///   <item>引擎<b>不采图</b>。相机由 VisionMaster 独占，运行时根本没有帧流到这一层；
///         引擎只吃"已经拿到的图"。</item>
///   <item>引擎<b>不写界面、不写库</b>。只把结论装进 <see cref="InspectionReport"/>。
///         落账、上屏、回 PLC 全在工作流；两处都写就会出现"界面显示了图、库里那条记录的
///         ImagePath 却是空"的两份真相，事后没法判断哪份对。</item>
///   <item>引擎<b>不抛异常给调用方</b>。平台没授权、流程文件损坏、报文格式不对，
///         都属于"现场一定会发生"的事，必须以 NG + 明确原因返回，由调用方记 ERROR。
///         抛出去的后果不是"程序崩了"而是"这一帧没结论"——它只会留下一条 Error 日志，
///         既不判也不入库，现场看到的是"这件产品凭空消失"，比一条 NG 难查得多。</item>
/// </list>
/// </para>
/// </summary>
public interface IInspectionEngine : IDisposable
{
    /// <summary>引擎标识（写进日志与记录，用来回答"这件是谁判的"）</summary>
    string Name { get; }

    /// <summary>
    /// 执行一次检测。
    ///
    /// <para>
    /// <b>前置条件</b>：<paramref name="request"/> 非空，且 <c>Image</c> 的所有权仍归调用方。
    /// 实现只能读它，<b>不得 Dispose</b> —— 调用方可能还要拿同一张图做标注与上屏，
    /// 引擎把它释放掉会表现为"偶发 ObjectDisposedException"，且只在有图时才复现。
    /// </para>
    /// <para>
    /// <b>副作用</b>：无。不写库、不存图、不动 UI、不改 <paramref name="request"/>。
    /// 这条约束是为了让"同一帧被调两次"不会产生两条记录。
    /// </para>
    /// <para>
    /// <b>线程</b>：在调用方线程同步执行（VM 推送路径上是收包线程，离线跑批是主线程），
    /// 实现自己保证并发安全；不做 async 是因为引擎调用是 CPU 密集且必须串行。
    /// </para>
    /// </summary>
    /// <param name="request">一帧图像及其上下文</param>
    /// <returns>
    /// 必定非空。失败时返回 <c>IsOk = false</c> 且原因写在 <c>ResultKeys.Engine</c>
    /// 这一项里的 <see cref="InspectionReport"/>（用 <see cref="InspectionReport.Failure"/> 造）；
    /// <see cref="InspectionReport.Results"/> 为空同样按 NG 处理 —— 没检 ≠ 合格。
    /// </returns>
    InspectionReport Inspect(InspectionRequest request);

}

/// <summary>
/// 一次检测的输入。
///
/// <para>
/// 只有三样东西：图、触发来源、图路径。刻意不放 ROI / 标定参数 ——
/// 这些已经归引擎自己所有（VM 在 .sol 里），
/// 从调用方再传一遍等于让两个地方都能改同一件事，早晚对不上。
/// </para>
///
/// <para>
/// <b>现状</b>：这是"主动检测"那条路的入参，而 VM 模式下那条路已经被
/// <c>InspectionWorkflow.RunOnceAsync</c> 无条件短路（相机归 VM 后，主动检测整条路都没了），
/// 所以 <c>src/</c> 里<b>没有生产代码构造它</b>，全仓也没有第二个构造点
/// （曾经唯一的构造方 <c>tools/GoldenSetRunner</c> 已于 2026-10-27 随 ONNX 一起删除）。
/// 不要在它上面加新的业务字段 —— 要加就加到上游（报文 / 配方），
/// 否则等于给一条运行时走不到的路加功能。
/// </para>
/// </summary>
public sealed class InspectionRequest
{
    /// <summary>原始图像。所有权归调用方，引擎不得释放。</summary>
    public required Mat Image { get; init; }

    /// <summary>触发来源：Manual / Timer / PLC</summary>
    public string TriggerSource { get; init; } = "Manual";

    /// <summary>
    /// 原图路径（可为空）。相机层删除后，这里只可能由离线跑批或测试填值 ——
    /// 运行时的图在 VM 那边，路径是随报文过来的，不经过这个字段。
    /// </summary>
    public string ImagePath { get; init; } = "";
}
