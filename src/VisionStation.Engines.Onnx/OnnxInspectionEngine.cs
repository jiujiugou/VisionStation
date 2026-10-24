using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionStation.Core.Engines;
using VisionStation.Core.Models;

namespace VisionStation.Engines.Onnx;

/// <summary>
/// ONNX YOLO 检测引擎 —— 把 <see cref="YoloDetector"/> 的框转成一份可入库的报告。
///
/// 适用边界（写在代码里，免得以后被当成"万能缺陷检测"）：
///   · 只做**外观缺陷**，不做尺寸测量、不做定位、不做有无判断
///     —— YOLO 给出的是"哪里有疑似缺陷"，量不出 12.68mm
///   · 判定规则：检出任意缺陷 → NG。这是 NEU-DET 六类缺陷的语义（六类都是不良），
///     换模型（如做分类或做有无）时必须改这里的判定，不能沿用
///   · 没有平台授权、也不想买授权的场景用它；要测量就上 VM 流程
///
/// 为什么用 ONNX 而不是 VM 的深度学习模块：
///   VM 的深度学习工具集需要单独授权，而 ONNX Runtime 免费、可嵌入、不绑平台。
///   预算紧或要部署到没有 VM 授权的机器上时，这是现成的第二条路。
/// </summary>
public sealed class OnnxInspectionEngine : IInspectionEngine
{
    private readonly ILogger? _logger;

    /// <summary>模型绝对路径（重载时要用，不能只在构造里用一次）</summary>
    private readonly string _modelPath;

    /// <summary>配方参数（重载时重建推理器要用）</summary>
    private readonly OnnxRecipeData _options;

    /// <summary>推理器。可换 —— 重载模型时整个替换，见 <see cref="Reload"/></summary>
    private YoloDetector _detector;

    /// <summary>推理器替换与推理互斥。推理是热路径，但这个锁只挡"人工重载"，不挡帧间并发</summary>
    private readonly object _gate = new();

    // 不同类别用不同颜色，方便现场一眼看出是哪类缺陷（BGR）
    private static readonly Scalar[] ClassColors =
    {
        new(0, 0, 255),      // crazing          红
        new(0, 165, 255),    // inclusion        橙
        new(0, 255, 255),    // patches          黄
        new(255, 0, 0),      // pitted_surface   蓝
        new(255, 0, 255),    // rolled-in_scale  品红
        new(0, 255, 0)       // scratches        绿
    };

    /// <param name="modelPath">模型绝对路径（由组合根按配方解析好）</param>
    /// <param name="options">模型参数（阈值、类别、最大框数）</param>
    /// <param name="logger">可选日志</param>
    public OnnxInspectionEngine(string modelPath, OnnxRecipeData options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _modelPath = modelPath;
        _options = options;
        _detector = new YoloDetector(
            modelPath,
            (float)options.Confidence,
            (float)options.Nms,
            options.Classes,
            options.MaxDetections);

        ModelPath = modelPath;
    }

    /// <inheritdoc />
    public string Name => "OnnxYolo";

    /// <summary>实际使用的模型路径（日志与自检用）</summary>
    public string ModelPath { get; }

    /// <inheritdoc />
    public InspectionReport Inspect(InspectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            IReadOnlyList<Detection> detections;
            double inferenceMs;

            // 只有"重载模型"会换 _detector，锁只在这两种操作之间互斥
            lock (_gate)
            {
                detections = _detector.Detect(request.Image, out inferenceMs);
            }

            var result = BuildResult(detections, inferenceMs);

            var report = new InspectionReport
            {
                Timestamp = DateTime.Now,
                TriggerSource = request.TriggerSource,
                ImagePath = request.ImagePath,
                Engine = Name
            };
            report.SetToolResult(ResultKeys.Defect, result);

            return report;
        }
        catch (Exception ex)
        {
            // 模型缺失、维度不符、内存不足都在这里兜住：一帧报废，但不能让进程死
            _logger?.LogError(ex, "[Onnx] 检测失败 模型={Model}", ModelPath);
            return InspectionReport.Failure(Name, $"ONNX 检测失败: {ex.Message}",
                request.TriggerSource, request.ImagePath);
        }
    }

    /// <summary>框 → 报告项。抽出来是为了能脱离模型单测判定与摘要。</summary>
    private static ToolResult BuildResult(IReadOnlyList<Detection> detections, double inferenceMs)
    {
        var overlays = new List<DrawCommand>(detections.Count);
        var perClass = new Dictionary<string, int>();
        float maxScore = 0f;

        foreach (var det in detections)
        {
            perClass[det.ClassName] = perClass.GetValueOrDefault(det.ClassName) + 1;
            if (det.Score > maxScore) maxScore = det.Score;

            overlays.Add(new DrawRect
            {
                Rect = det.Box,
                Color = ClassColors[det.ClassId % ClassColors.Length],
                Label = $"{det.ClassName} {det.Score:F2}",
                Thickness = 2
            });
        }

        int total = overlays.Count;
        var metrics = new Dictionary<string, double>
        {
            ["DefectCount"] = total,
            ["MaxScore"] = Math.Round(maxScore, 4),
            ["InferenceMs"] = Math.Round(inferenceMs, 1)
        };
        foreach (var kv in perClass)
            metrics[$"Count_{kv.Key}"] = kv.Value;

        if (total == 0)
        {
            return new ToolResult
            {
                IsOk = true,
                Summary = $"未检出缺陷（推理 {inferenceMs:F0}ms）",
                Metrics = metrics
            };
        }

        var detail = string.Join(" ", perClass.Select(kv => $"{kv.Key}:{kv.Value}"));
        return new ToolResult
        {
            IsOk = false,
            Summary = $"检出缺陷 {total} 处 [{detail}] 最高分 {maxScore:F2}",
            Metrics = metrics,
            Overlays = overlays
        };
    }

    /// <summary>
    /// 重新加载模型 —— 换权重文件（重训之后）不用重启程序。
    ///
    /// 先建新的、成功了再换旧的：建失败时旧推理器必须还在，
    /// 否则一次失败的"换模型"会把一个本来能用的引擎变成不可用。
    /// </summary>
    public string? Reload()
    {
        try
        {
            var rebuilt = new YoloDetector(
                _modelPath,
                (float)_options.Confidence,
                (float)_options.Nms,
                _options.Classes,
                _options.MaxDetections);

            YoloDetector old;
            lock (_gate)
            {
                old = _detector;
                _detector = rebuilt;
            }

            old.Dispose();
            _logger?.LogInformation("[Onnx] 模型已重载：{Model}", _modelPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Onnx] 模型重载失败 模型={Model}", _modelPath);
            return $"模型重载失败: {ex.Message}（原模型仍在用）";
        }
    }

    /// <inheritdoc />
    public void Dispose() => _detector.Dispose();
}
