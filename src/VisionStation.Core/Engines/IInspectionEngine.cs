using OpenCvSharp;
using VisionStation.Core.Models;

namespace VisionStation.Core.Engines;

/// <summary>
/// 检测引擎 —— 本系统唯一的"大脑"抽象。
///
/// 为什么这里要一个接口（而不是像自研 Pipeline 时代那样直接 new 具体类）：
///   系统里**确实存在两个引擎**，而且必须能按产品切换：
///     · <c>Vm</c>   —— 海康 VisionMaster 流程（.sol），定位/测量/传统缺陷都在平台里配
///     · <c>Onnx</c> —— ONNX YOLO 缺陷检测，无平台授权时的兜底
///   两个实现都要活到进程结束、都由配方选择，这是真实的替换点，不是为 mock 造的接口。
///
/// 边界约定（比接口本身更重要）：
///   1. 引擎**不做采图**。运行时帧由平台推送（VM 存盘 + 报文带路径），
///      离线跑批由 GoldenSetRunner 直接读文件 —— 引擎只吃已经拿到的图。
///   2. 引擎**不写界面、不写库**。只把结论装进 <see cref="InspectionReport"/>。
///   3. 引擎**不抛异常给调用方**。平台没授权、模型缺失、流程文件损坏，
///      都属于"现场一定会发生"的事，必须以 NG + 明确原因返回，并由调用方记 ERROR。
///      抛异常会让一帧检测变成一次崩溃，而产线要的是"这一件没判出来，别把线停了还查不到原因"。
/// </summary>
public interface IInspectionEngine : IDisposable
{
    /// <summary>引擎标识（写进日志与记录，用来回答"这件是谁判的"）</summary>
    string Name { get; }

    /// <summary>
    /// 执行一次检测。
    /// </summary>
    /// <param name="request">一帧图像及其上下文</param>
    /// <returns>
    /// 必定非空。失败时返回 <c>IsOk = false</c> 且带原因的 <see cref="InspectionReport"/>；
    /// <see cref="InspectionReport.Results"/> 为空同样按 NG 处理（没检 ≠ 合格）。
    /// </returns>
    InspectionReport Inspect(InspectionRequest request);

    /// <summary>
    /// 重新加载引擎资源（`.sol` 流程 / ONNX 模型）。
    ///
    /// 为什么必须有：调流程是二次开发的日常 —— 工程师在 VM 客户端改一个公差、存盘，
    /// 回到本程序必须能立刻看到效果。没有它，每改一次就要关程序重开，
    /// 而重启要走完整个自检与设备初始化，改十次就是十遍。
    ///
    /// 约定：
    ///   1. 实现必须自己保证并发安全（重载期间有帧进来，应得到 NG 而不是崩溃）；
    ///   2. 重载失败不得让引擎进入"看起来能用"的状态 —— 后续帧必须继续返回 NG + 原因；
    ///   3. 返回 null 表示重载成功；否则返回可直接展示给工程师的失败原因（不抛异常）。
    /// </summary>
    string? Reload();
}

/// <summary>
/// 可选能力：扫描引擎资源里**实际存在**的输出项。
///
/// 为什么单独一个接口而不是塞进 <see cref="IInspectionEngine"/>：
/// 只有 VM 流程有"可枚举的模块与输出"这个概念（ONNX 的输出是固定的 DefectCount 那几项）。
/// 塞进主接口会让 ONNX 引擎被迫实现一个它没有意义的方法。
///
/// 解决的问题：配方里的 `vm.outputs` 现在要人工抄模块名和输出名，
/// 抄错一个字符的后果是"找不到模块「圆查找1」"，而且要到第一帧才报。
/// 有了扫描，一次生成本地文件，复制粘贴即可。
/// </summary>
public interface IOutputScanner
{
    /// <summary>
    /// 扫描指定模块的输出项。
    ///
    /// 为什么按名字扫、不做"列出全部模块"：
    /// SDK 的 `GetAllModuleList` 只返回模块 ID 与流程 ID（`ModuInfo` 里没有名字），
    /// 拿不到模块名就没法写进配方。而配方里本来就声明了模块名 ——
    /// 用它去核对"这个名字对不对、这个模块有哪些输出"才是真正要解决的问题。
    /// </summary>
    /// <param name="moduleNames">要扫描的模块名（来自配方的 vm.outputs[].module）</param>
    OutputScanResult Scan(IReadOnlyList<string> moduleNames);
}

/// <summary>扫描结果。失败时 <see cref="Error"/> 非空，<see cref="Modules"/> 为空。</summary>
public sealed record OutputScanResult(
    IReadOnlyList<ScannedModule> Modules,
    string? Error)
{
    /// <summary>失败结果</summary>
    public static OutputScanResult Failed(string error) => new(Array.Empty<ScannedModule>(), error);
}

/// <summary>一个模块及其输出项</summary>
/// <param name="Name">模块名（写成配方里的 vm.outputs[].module）</param>
/// <param name="Outputs">该模块的输出项</param>
/// <param name="Error">该模块扫描失败的原因（如 .sol 里没有这个模块名）</param>
public sealed record ScannedModule(
    string Name,
    IReadOnlyList<ScannedOutput> Outputs,
    string? Error = null);

/// <summary>一个输出项</summary>
/// <param name="Name">输出名（写成配方里的 vm.outputs[].io）</param>
/// <param name="RuntimeType">运行时类型名（VmIOFloat / VmIOCircle …），用于提示 ioKind</param>
public sealed record ScannedOutput(string Name, string RuntimeType);

/// <summary>
/// 标记：判定由**外部系统主动推送**，本引擎不响应"给图 → 出报告"。
///
/// 有它的原因：TCP 中转模式下 VisionMaster 自己采图、自己判定、把结果推过来，
/// 上位机的自动循环若照旧每 2 秒调一次 <see cref="IInspectionEngine.Inspect"/>，
/// 就会往数据库里灌一堆"VM 模式下不支持主动检测"的假 NG。
/// 那种记录比不记录更糟 —— 事后统计良率会把它算成不良品。
///
/// 所以工作流在跑主动检测前先问一句：这个引擎是不是推驱动的？是就直接跳过，不入库。
/// </summary>
public interface IPushDrivenEngine
{
}

/// <summary>
/// 一次检测的输入。
///
/// 只有三样东西：图、触发来源、图路径。刻意不放 ROI / 标定参数 ——
/// 这些已经归引擎自己所有（VM 在 .sol 里、ONNX 在整图上），
/// 从调用方再传一遍等于让两个地方都能改同一件事，早晚对不上。
/// </summary>
public sealed class InspectionRequest
{
    /// <summary>原始图像。所有权归调用方，引擎不得释放。</summary>
    public required Mat Image { get; init; }

    /// <summary>触发来源：Manual / Timer / PLC</summary>
    public string TriggerSource { get; init; } = "Manual";

    /// <summary>原图路径（File 相机下有值，用于追溯与 VM 喂图）</summary>
    public string ImagePath { get; init; } = "";
}
