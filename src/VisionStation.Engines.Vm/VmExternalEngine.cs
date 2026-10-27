using VisionStation.Core.Engines;
using VisionStation.Core.Models;

namespace VisionStation.Engines.Vm;

/// <summary>
/// "判定在 VisionMaster 那边"时的引擎占位。
///
/// <para>
/// <b>定位</b>：它在 <see cref="IInspectionEngine"/> 这个"给图 → 出报告"的契约上，
/// 表达一件方向相反的事 —— 图在 VM 那边、判定也在 VM 那边，结果是被推过来的。
/// 它<b>不做任何检测</b>，也<b>不加载 SDK</b>，这正是重点。
/// </para>
///
/// <para>
/// <b>为什么还需要它</b>：工作流的下半段（显示 / 入库 / 回 PLC）对所有驱动方式都是同一条，
/// 所以 VM 模式仍然要占住一个引擎位 —— 否则 <c>InspectionWorkflow</c> 就得为"没有引擎"
/// 写一条特殊分支，而那条分支没人会去测。
/// </para>
///
/// <para>
/// <b>归属与线程</b>：由 <c>InspectionEngineFactory.Create</c> 在组合根 new 一次，
/// 注册为单例，活到进程结束。无状态（只有一个构造时算好的提示字符串），
/// 所以随便哪个线程调都安全；<see cref="Dispose"/> 是空实现，因为确实没有可释放的东西。
/// </para>
///
/// <para>
/// <b>失败行为</b>：<see cref="Inspect"/> 永远返回
/// <see cref="InspectionReport.Failure"/>（NG + 原因），<b>不抛业务异常</b> ——
/// 这条契约不能破：万一有代码绕过推驱动判断调进来，抛异常会让那一帧没有记录，
/// 而返回 NG 至少会在库里留一条可查的痕迹。
/// 唯一会抛的是 <c>request</c> 为 null（<see cref="ArgumentNullException"/>），
/// 那属于调用方 bug，不是"引擎故障"。
/// </para>
///
/// <para>
/// <b>不引用任何海康 SDK，任何机器都能编译、能单测</b>。这不是洁癖，是硬约束：
/// VM 4.2 的 SDK 是 net461 + <c>System.Runtime.Remoting</c>，在 .NET 8 进程里
/// 连静态构造都过不去（实测进程直接崩 <c>0xC000000D</c>，还要求管理员权限去开 SCM），
/// 所以本工程改走 TCP 中转（VM 当客户端推、本程序当服务端收）。
/// 结果是这个程序集只依赖 Core，CI 与求职者的笔记本上都能跑。
/// </para>
///
/// <para>
/// <b>为什么这个引擎永远不主动检测</b>：检测由 VisionMaster 自己跑、结果主动推过来，
/// 本引擎没有相机、没有流程、也没有回推通道。所以 <see cref="Inspect"/> 恒返回 NG；
/// 上层（控制面板按钮、自动循环）在调它之前就已经说明"检测由平台执行"。
/// 这里不做"引擎类型分流"——运行时只有这一个引擎，"按能力判断"没有任何第二种取值可对照
/// （2026-10-27 F-10 拍板，原来的 <c>IPushDrivenEngine</c> 标记已删除：它在真正的
/// 执行路径上从未起过作用，只是看起来在工作）。
/// </para>
/// </summary>
public sealed class VmExternalEngine : IInspectionEngine
{
    /// <summary>
    /// 构造时算好的失败原因（含链路描述）。存成字段而不是每次拼：
    /// 这段文字会在"人工误触主动检测"时逐帧产生，每帧拼一次字符串没有意义。
    /// </summary>
    private readonly string _reason;

    /// <param name="linkDescription">链路描述（如 "TCP :9100，等待 VisionMaster 推送"），会写进提示</param>
    public VmExternalEngine(string? linkDescription = null)
    {
        _reason = "VM 模式下检测由 VisionMaster 主动推送，上位机不取图、不主动触发"
                  + (string.IsNullOrWhiteSpace(linkDescription) ? "" : $"（{linkDescription}）");
    }

    /// <summary>
    /// 引擎标识，写进 <c>InspectionReport.Engine</c> 与数据库的 EngineName 列。
    /// 用 "VisionMaster" 而不是 "Vm"：库里这个值是给人看的，要与配方里的机读值区分开。
    /// </summary>
    public string Name => "VisionMaster";

    /// <summary>
    /// 主动检测入口 —— <b>这里永远拒绝</b>。
    ///
    /// <para>
    /// 前置条件：<paramref name="request"/> 非空（null 视为调用方 bug，直接抛）。
    /// 副作用：无 —— 不读图、不释放 <c>request.Image</c>、不发消息、不写库。
    /// </para>
    /// <para>
    /// <b>实际上几乎不会被调用</b>：控制面板在点"开始检测"时直接说明"检测由平台执行"，
    /// 自动循环那条路的 <c>RunOnceAsync</c> 也无条件返回 null；所以操作员看到的那句提示
    /// 来自 <c>ControlPanelViewModel</c>，不是这里。本方法的价值是"第二道防线"：
    /// 任何新代码绕过那两处时，拿到的还是一条可解释的 NG，而不是一个空引用。
    /// </para>
    /// </summary>
    public InspectionReport Inspect(InspectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return InspectionReport.Failure(Name, _reason, request.TriggerSource, request.ImagePath);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // 没有非托管资源：这个类连 SDK 都不引用
    }
}
