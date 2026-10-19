using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;

namespace VisionStation.Core;

/// <summary>
/// 检测流水线 —— 整个 VisionStation 的核心编排器
///
/// 职责（只做一件事）：
///   接收图像 + ROI列表 → 遍历 IVisionTool 列表逐工具执行 → 聚合所有 ToolResult 生成 InspectionReport
///
/// 设计原则：
///   1. Pipeline 不知道每个 Tool 内部做什么 —— 只知道 IVisionTool 接口
///   2. 加新工具：写一个类实现 IVisionTool → 注册到 Pipeline → Pipeline 其他代码一行不动
///   3. 工具和 ROI 的匹配：三层策略（名字 → 索引 → 重用末位）
///
/// 使用方式：
///   var pipeline = new Pipeline(new List<IVisionTool> { presTool, dimTool, defTool });
///   var report = pipeline.Run(image, rois, calibConfig);
///
/// 为什么需要这个类？
///   没有 Pipeline，MainViewModel 里就要 foreach 手动调每个 Tool —— 耦合了工具创建和执行顺序，
///   Pipeline 把"按顺序执行工具链"这个策略单独封装，MainViewModel 一行 Run 就行。
/// </summary>
public class Pipeline
{
    // 工具链（由 DI 注入，ToolFactory 创建）
    private readonly IList<IVisionTool> _tools;

    // 日志（可空，不影响运行）
    private readonly ILogger<Pipeline>? _logger;

    /// <summary>
    /// 构造 Pipeline。
    /// tools 列表的顺序 = 执行顺序。每个 Tool 的 Name 应与对应 ROI 的 Name 匹配。
    /// </summary>
    /// <param name="tools">工具列表（不能为空，否则 Run 什么都不做）</param>
    /// <param name="logger">可选日志记录器</param>
    public Pipeline(IEnumerable<IVisionTool> tools, ILogger<Pipeline>? logger = null)
    {
        _tools = tools.ToList();
        _logger = logger;
    }

    /// <summary>
    /// 执行一次完整检测 —— 整个系统的核心入口
    ///
    /// 流程：
    ///   1. 彩色原图 → 灰度图（所有 Tool 统一用灰度，省得每个 Tool 自己转）
    ///   2. 记录每个 ROI 的裁切尺寸
    ///   3. 遍历 tools 列表，逐个执行：
    ///      a. 匹配 ROI（名字 → 索引 → 重用末位）
    ///      b. tool.Execute(gray, roi) → ToolResult
    ///      c. 异常捕获 → ToolResult.Ng（不中断后续工具）
    ///   4. 所有结果存入 InspectionReport
    /// </summary>
    /// <param name="image">原始彩色图像（Mat BGR 格式）</param>
    /// <param name="rois">ROI 区域列表（名称与 Tool.Name 对应）</param>
    /// <param name="calibConfig">标定配置（传给 DimensionTool）</param>
    /// <param name="triggerSource">触发来源标记（日志用）</param>
    /// <param name="imagePath">原图路径（日志用）</param>
    /// <returns>包含所有工具执行结果的 InspectionReport</returns>
    public InspectionReport Run(
        Mat image,
        List<RoiRegion> rois,
        CalibrationConfig calibConfig,
        string triggerSource = "Manual",
        string imagePath = "")
    {
        // ══ 转灰度（所有 Tool 共用，不在每个 Tool 里重复转）══
        // 黑白工业相机直出 8UC1，对它调 BGR2GRAY 会抛异常 —— 每帧都崩，检测直接瘫痪。
        // 所以按通道数分支，单通道直接复用。
        using var gray = new Mat();
        switch (image.Channels())
        {
            case 1:
                image.CopyTo(gray);
                break;
            case 4:
                Cv2.CvtColor(image, gray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
                break;
        }

        // ══ ROI 字典（按名称索引，O(1) 查找）══
        // 不能用 ToDictionary：ROI 重名会抛 ArgumentException，整个 Run 直接失败 ——
        // 而 ROI 编辑器的自动命名（ROI-{数量+1}）删一个再加一个就会撞名。
        // 容错策略：保留先出现的那个（顺序有意义），重名记警告但不崩。
        var roiMap = new Dictionary<string, RoiRegion>(rois.Count);
        foreach (var r in rois)
        {
            if (!roiMap.TryAdd(r.Name, r))
                _logger?.LogWarning(
                    "[Pipeline] ROI 名称重复：\"{Name}\"，已忽略后出现的那一个（请检查配方）", r.Name);
        }

        // ══ 新建报告 ══
        var report = new InspectionReport
        {
            Timestamp = DateTime.Now,
            TriggerSource = triggerSource,
            ImagePath = imagePath
        };

        // ══ 记录每个 ROI 的裁切尺寸（调试用）══
        foreach (var kv in roiMap)
        {
            _logger?.LogInformation(
                "[ROI裁切] {Name}: 原图尺寸:{W}x{H}px → ROI区域:({X},{Y}) {RoiW}x{RoiH}px",
                kv.Key, image.Width, image.Height,
                kv.Value.X, kv.Value.Y, kv.Value.Width, kv.Value.Height);
        }

        // ══ 遍历工具链，逐个执行 ══
        for (int i = 0; i < _tools.Count; i++)
        {
            var tool = _tools[i];
            RoiRegion? roi = null;

            // ── 三层 ROI 匹配策略 ──
            // 策略1：按 Tool.Name 匹配同名 ROI（如 Tool "Presence" → ROI "Presence"）
            if (roiMap.TryGetValue(tool.Name, out var namedRoi))
                roi = namedRoi;
            // 策略2：按顺序匹配（第 i 个 Tool → 第 i 个 ROI）
            else if (i < rois.Count)
                roi = rois[i];
            // 策略3：ROI 不够用时，重用最后一个（一框多用场景）
            else if (rois.Count > 0)
                roi = rois[^1];

            if (roi == null)
            {
                _logger?.LogWarning("[Pipeline] {Tool}: 未匹配到 ROI（共{Total}个）",
                    tool.Name, rois.Count);
                continue;
            }

            _logger?.LogInformation("[Pipeline] {Tool} → ROI[{Idx}] \"{RoiName}\" ({X},{Y}) {W}x{H}",
                tool.Name, i, roi.Name, roi.X, roi.Y, roi.Width, roi.Height);

            // ── 执行工具，异常不中断后续工具 ──
            ToolResult result;
            try
            {
                result = tool.Execute(gray, roi);
                _logger?.LogInformation("[Pipeline] {Tool}: {Result} {Summary}",
                    tool.Name, result.IsOk ? "OK" : "NG", result.Summary);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[Pipeline] {Tool} 执行异常", tool.Name);
                result = ToolResult.Ng($"{tool.Name}异常: {ex.Message}");
            }

            // 结果按工具名存储（方便 UI 层取用）
            report.SetToolResult(tool.Name, result);
        }

        return report;
    }
}
