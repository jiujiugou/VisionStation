using VisionStation.Core.Engines;
using VisionStation.Core.Models;

namespace VisionStation.Engines.Vm;

/// <summary>
/// "判定在 VisionMaster 那边"时的引擎占位。
///
/// 它**不做任何检测**，也**不加载 SDK** —— 这正是重点。
///
/// 存在的理由：<see cref="IInspectionEngine"/> 这个契约表达的是"给图 → 出报告"，
/// 而 TCP 中转模式下方向是反的：VM 自己采图、自己跑流程、主动把结果推过来，
/// 上位机是接收方。两条路的驱动方向不同，但工作流（存图 / 存库 / 回 PLC / 显示）
/// 是同一条，所以还是让 VM 模式占住这个引擎位，只是它的 Inspect 明确拒绝主动调用。
///
/// 它的价值在于**让错误可见**：如果界面上的"开始检测"按钮在 VM 模式下被点了，
/// 操作员会看到一句"VM 模式由 VM 主动推送结果"，而不是静默失败或者去采一张
/// 已经被 VM 独占的相机的图。
/// </summary>
public sealed class VmExternalEngine : IInspectionEngine, IPushDrivenEngine
{
    private readonly string _reason;

    /// <param name="linkDescription">链路描述（如 "TCP :9100，等待 VisionMaster 推送"），会写进提示</param>
    public VmExternalEngine(string? linkDescription = null)
    {
        _reason = "VM 模式下检测由 VisionMaster 主动推送，上位机不取图、不主动触发"
                  + (string.IsNullOrWhiteSpace(linkDescription) ? "" : $"（{linkDescription}）");
    }

    /// <inheritdoc />
    public string Name => "VisionMaster";

    /// <inheritdoc />
    public InspectionReport Inspect(InspectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return InspectionReport.Failure(Name, _reason, request.TriggerSource, request.ImagePath);
    }

    /// <summary>
    /// 外部模式下没有"重载方案"的概念 —— 流程在 VM 客户端里，改完在 VM 里重跑即可。
    /// 返回 null 表示"没有需要报告的错误"。
    /// </summary>
    public string? Reload() => null;

    /// <inheritdoc />
    public void Dispose()
    {
        // 没有非托管资源：这个类连 SDK 都不引用
    }
}
