using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using VisionStation.Core.Models;

namespace VisionStation.Core.Tools;

/// <summary>
/// YOLO 深度学习缺陷检测工具 —— 加载 ONNX 模型，在 ROI 内做目标检测
///
/// 为什么要有它？
///   DefectTool 是传统方法（阈值 + 连通域），优点是确定性、可解释、零依赖，
///   缺点是依赖"缺陷比背景暗"这个前提，光照一变、材质一换就废。
///   YOLO 版本用带标注训练出来的模型做判断，对纹理类缺陷（龟裂、氧化皮、麻点）
///   鲁棒性明显更好，代价是需要离线训练 + 一个 12MB 的权重文件。
///
///   两者并存不冲突：同一个 Pipeline 里，传统工具负责尺寸/存在性，
///   这个工具负责外观缺陷分类，各自匹配自己的 ROI。
///
/// 模型信息（NEU-DET 钢材表面缺陷，6 类）：
///   输入  1 x 3 x 640 x 640  float32，RGB，/255 归一化，HWC -> CHW
///   输出  1 x 10 x 8400      每列 = [cx, cy, w, h, cls0..cls5]
///   注意  YOLOv8 起**没有 objectness 通道**，置信度直接取 6 个类别分数的最大值。
///         这一点和 YOLOv5 不同，移植代码时最容易在这里写错。
///
/// 关于灰度输入：
///   IVisionTool 统一收灰度图（Pipeline 只 CvtColor 一次，省得每个工具重复转）。
///   YOLO 需要 3 通道，但 NEU-DET 本身就是灰度纹理数据，
///   所以 GRAY -> BGR -> RGB 得到的三个通道是相同的，语义上等价于原图，不会引入失真。
/// </summary>
public class OnnxYoloTool : IVisionTool, IDisposable
{
    /// <summary>模型输入边长（训练时 imgsz=640，导出时 fixed shape，改模型才改这里）</summary>
    private const int InputSize = 640;

    /// <summary>letterbox 填充灰度值 —— 114 是 ultralytics 的固定约定，不要改，改了精度会掉</summary>
    private const float PadValue = 114f;

    /// <summary>
    /// 工具名。必须与配方中 ROI 的 Name 对应。
    /// 做成可写属性是为了一个配方里能挂多个不同模型（如 "YoloSurface" / "YoloWeld"）。
    /// </summary>
    public string Name { get; set; } = "Yolo";

    /// <summary>ONNX 模型绝对路径</summary>
    public string ModelPath { get; }

    /// <summary>
    /// 置信度阈值：低于此分数的候选框直接丢弃（0.25 是工业缺陷检测的常用起点）。
    /// 越界必须立刻失败（F-C09）：阈值 &gt; 1 会让所有候选被过滤掉，
    /// 工具返回"未检出缺陷"= OK —— 模型什么都没判，系统却报合格。
    /// </summary>
    public float ConfThreshold
    {
        get => _confThreshold;
        set
        {
            if (float.IsNaN(value) || value <= 0f || value > 1f)
                throw new ArgumentOutOfRangeException(nameof(ConfThreshold), value,
                    "置信度阈值必须在 (0,1] 内；填 1.5 之类会让所有检测被静默丢弃");
            _confThreshold = value;
        }
    }
    private float _confThreshold = 0.25f;

    /// <summary>NMS 的 IoU 阈值：重叠度高于此值的同类框只保留分数最高的</summary>
    public float NmsThreshold
    {
        get => _nmsThreshold;
        set
        {
            if (float.IsNaN(value) || value <= 0f || value > 1f)
                throw new ArgumentOutOfRangeException(nameof(NmsThreshold), value,
                    "NMS 阈值必须在 (0,1] 内");
            _nmsThreshold = value;
        }
    }
    private float _nmsThreshold = 0.45f;

    /// <summary>
    /// 单帧最大保留框数（防止异常图像产生上千个框拖垮 UI）。
    /// 必须 ≥ 1：填 0 会把所有框裁掉，等价于"永远没有缺陷"。
    /// </summary>
    public int MaxDetections
    {
        get => _maxDetections;
        set
        {
            if (value < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxDetections), value,
                    "单帧最大保留框数必须 ≥ 1；填 0 会让所有检测结果被裁掉并报合格");
            _maxDetections = value;
        }
    }
    private int _maxDetections = 100;

    /// <summary>类别名，顺序必须与训练时 data.yaml 的 names 完全一致（不能为空）</summary>
    public string[] ClassNames
    {
        get => _classNames;
        set
        {
            if (value == null || value.Length == 0)
                throw new ArgumentOutOfRangeException(nameof(ClassNames),
                    "类别名不能为空：没有类别名就无法解码任何候选框");
            _classNames = value;
        }
    }
    private string[] _classNames =
    {
        "crazing", "inclusion", "patches",
        "pitted_surface", "rolled-in_scale", "scratches"
    };

    // ══ 模型会话 ══
    // InferenceSession 的构造要解析图 + 做算子融合，约 100~300ms，绝不能每帧新建；
    // 但它也不是线程安全的创建过程，所以用双检锁做一次惰性初始化。
    private readonly object _sessionLock = new();
    private InferenceSession? _session;

    /// <summary>是否已完成模型加载（UI 可用来提示"模型未就绪"）</summary>
    public bool IsLoaded
    {
        get { lock (_sessionLock) return _session != null; }
    }

    /// <summary>
    /// 创建 YOLO 检测工具
    /// </summary>
    /// <param name="modelPath">ONNX 模型路径（找不到会在首次执行时抛异常）</param>
    /// <param name="confThreshold">置信度阈值</param>
    /// <param name="nmsThreshold">NMS IoU 阈值</param>
    public OnnxYoloTool(string modelPath, float confThreshold = 0.25f, float nmsThreshold = 0.45f)
    {
        ModelPath = modelPath;
        ConfThreshold = confThreshold;
        NmsThreshold = nmsThreshold;
    }

    /// <summary>
    /// 惰性创建推理会话 —— 第一次 Execute 时才真正加载模型，
    /// 避免应用启动时因为一个没配置的模型路径就整个崩掉。
    /// </summary>
    private InferenceSession EnsureSession()
    {
        if (_session != null) return _session;

        lock (_sessionLock)
        {
            if (_session != null) return _session;

            if (!File.Exists(ModelPath))
                throw new FileNotFoundException($"YOLO 模型文件不存在：{ModelPath}");

            var options = new SessionOptions
            {
                // ORT_ENABLE_ALL：启用算子融合 + 常量折叠，CPU 上通常能快 20~30%
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            // 线程数取一半核心：工业现场 CPU 还要留给采集线程和 UI，不能全占
            options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);

            _session = new InferenceSession(ModelPath, options);
            return _session;
        }
    }

    /// <summary>
    /// 在 ROI 区域内执行 YOLO 推理
    ///
    /// 处理流程：
    ///   1. 裁 ROI（灰度）
    ///   2. GRAY -> BGR -> RGB（YOLO 训练用的是 RGB）
    ///   3. letterbox 缩放：等比缩放到 640 内，右下角补 114 灰边
    ///      —— 不能直接 Resize，直接拉伸会改变缺陷的长宽比，模型没见过这种形变
    ///   4. 填进 DenseTensor：/255 归一化 + HWC -> CHW
    ///   5. 推理 → 解 [1,10,8400]
    ///   6. 置信度过滤 → 按类别分别 NMS
    ///   7. 坐标反算：letterbox -> ROI 局部 -> 原图（+roi.X/Y）
    /// </summary>
    /// <param name="grayImage">整幅灰度图</param>
    /// <param name="roi">检测区域（Name 匹配本工具的 Name）</param>
    /// <returns>
    /// OK：未检出缺陷；NG：检出缺陷或推理失败。
    /// Metrics：DefectCount / MaxScore / InferenceMs / Count_&lt;类别名&gt;
    /// </returns>
    public ToolResult Execute(Mat grayImage, RoiRegion roi)
    {
        if (roi.Width <= 0 || roi.Height <= 0)
            return ToolResult.Ng("ROI无效");

        using var roiMat = roi.Crop(grayImage);
        if (roiMat.Empty())
            return ToolResult.Ng("ROI无效");

        // ── 1. 灰度转 BGR 再转 RGB ──
        using var bgr = new Mat();
        using var rgb = new Mat();
        Cv2.CvtColor(roiMat, bgr, ColorConversionCodes.GRAY2BGR);
        Cv2.CvtColor(bgr, rgb, ColorConversionCodes.BGR2RGB);

        // ── 2. letterbox 到 640 ──
        var (padded, scale, padX, padY) = Letterbox(rgb);

        // ── 3. 组装输入张量 ──
        var input = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        FillTensor(padded, input);
        padded.Dispose();

        // ── 4. 推理 ──
        var session = EnsureSession();
        var inputName = session.InputMetadata.Keys.First();
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, input) };

        var sw = Stopwatch.StartNew();
        using var results = session.Run(inputs);
        sw.Stop();
        double inferenceMs = sw.Elapsed.TotalMilliseconds;

        // ── 5. 解码输出 ──
        var raw = ExtractCandidates(results);

        // ── 6. 置信度过滤 + 按类别 NMS ──
        var kept = FilterAndNms(raw);

        // ── 7. 坐标反算到原图，生成标注指令 ──
        var overlays = new List<DrawCommand>();
        var perClassCount = new Dictionary<string, int>();
        float maxScore = 0f;

        // 不同类别用不同颜色，方便现场一眼看出是哪类缺陷（BGR）
        var classColors = new[]
        {
            new Scalar(0, 0, 255),      // crazing          红
            new Scalar(0, 165, 255),    // inclusion        橙
            new Scalar(0, 255, 255),    // patches          黄
            new Scalar(255, 0, 0),      // pitted_surface   蓝
            new Scalar(255, 0, 255),    // rolled-in_scale  品红
            new Scalar(0, 255, 0)       // scratches        绿
        };

        foreach (var det in kept)
        {
            // letterbox 坐标 → ROI 坐标：先减填充、再除缩放比
            float x1 = (det.Cx - det.W / 2 - padX) / scale;
            float y1 = (det.Cy - det.H / 2 - padY) / scale;
            float w = det.W / scale;
            float h = det.H / scale;

            // 裁到 ROI 边界内（letterbox 反算可能溢出几个像素）
            int left = Math.Max(0, (int)MathF.Round(x1));
            int top = Math.Max(0, (int)MathF.Round(y1));
            int right = Math.Min(roi.Width, (int)MathF.Round(x1 + w));
            int bottom = Math.Min(roi.Height, (int)MathF.Round(y1 + h));
            if (right <= left || bottom <= top) continue;

            // ROI 局部 → 原图坐标
            var rect = new Rect(left + roi.X, top + roi.Y, right - left, bottom - top);

            string clsName = det.ClassId < ClassNames.Length
                ? ClassNames[det.ClassId]
                : $"cls{det.ClassId}";

            perClassCount[clsName] = perClassCount.GetValueOrDefault(clsName) + 1;
            if (det.Score > maxScore) maxScore = det.Score;

            overlays.Add(new DrawRect
            {
                Rect = rect,
                Color = classColors[det.ClassId % classColors.Length],
                Label = $"{clsName} {det.Score:F2}",
                Thickness = 2
            });
        }

        // ── 8. 汇总 ──
        int total = overlays.Count;
        var metrics = new Dictionary<string, double>
        {
            ["DefectCount"] = total,
            ["MaxScore"] = Math.Round(maxScore, 4),
            ["InferenceMs"] = Math.Round(inferenceMs, 1)
        };
        foreach (var kv in perClassCount)
            metrics[$"Count_{kv.Key}"] = kv.Value;

        if (total == 0)
            return new ToolResult
            {
                IsOk = true,
                Summary = $"未检出缺陷（推理 {inferenceMs:F0}ms）",
                Metrics = metrics
            };

        var detail = string.Join(" ", perClassCount.Select(kv => $"{kv.Key}:{kv.Value}"));
        return new ToolResult
        {
            IsOk = false,
            Summary = $"检出缺陷 {total} 处 [{detail}] 最高分 {maxScore:F2}",
            Metrics = metrics,
            Overlays = overlays
        };
    }

    // ═══════════════════════════════════════════════════════
    //  内部实现
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// letterbox 等比缩放 —— YOLO 的标准预处理
    ///
    /// 为什么不能直接 Resize(640,640)？
    ///   假设 ROI 是 800x200 的细长条，硬拉伸成正方形会把缺陷横向压扁 4 倍，
    ///   模型见过的是"正常比例"的缺陷，形变后特征全毁，基本检不出东西。
    ///   letterbox 保住长宽比，多出来的部分补灰边（114），模型训练时见惯了这个灰边。
    /// </summary>
    private static (Mat Padded, float Scale, float PadX, float PadY) Letterbox(Mat src)
    {
        float r = Math.Min((float)InputSize / src.Width, (float)InputSize / src.Height);
        int newW = (int)MathF.Round(src.Width * r);
        int newH = (int)MathF.Round(src.Height * r);

        using var resized = new Mat();
        Cv2.Resize(src, resized, new Size(newW, newH), 0, 0, InterpolationFlags.Linear);

        // 两侧均分填充（ultralytics 的算法，取整后可能一侧多 1 像素，无影响）
        float dw = (InputSize - newW) / 2f;
        float dh = (InputSize - newH) / 2f;
        int top = (int)MathF.Round(dh - 0.1f);
        int bottom = (int)MathF.Round(dh + 0.1f);
        int left = (int)MathF.Round(dw - 0.1f);
        int right = (int)MathF.Round(dw + 0.1f);

        var padded = new Mat();
        Cv2.CopyMakeBorder(resized, padded, top, bottom, left, right,
            BorderTypes.Constant, new Scalar(PadValue, PadValue, PadValue));

        return (padded, r, left, top);
    }

    /// <summary>
    /// 把 8UC3 的 RGB 图填进 NCHW 张量，同时做 /255 归一化。
    /// 用 Span 直接写 buffer，比 tensor[0,c,y,x] 逐元素索引快一个量级。
    /// </summary>
    private static void FillTensor(Mat rgb, DenseTensor<float> tensor)
    {
        const int plane = InputSize * InputSize;
        var buffer = tensor.Buffer.Span;

        // 拆通道：OpenCV 是 HWC 交错存储，模型要 CHW 平面存储，拆开最好写
        Cv2.Split(rgb, out var channels);
        try
        {
            for (int c = 0; c < 3; c++)
            {
                channels[c].GetArray(out byte[] bytes);
                int offset = c * plane;
                for (int i = 0; i < plane && i < bytes.Length; i++)
                    buffer[offset + i] = bytes[i] / 255f;
            }
        }
        finally
        {
            foreach (var ch in channels) ch.Dispose();
        }
    }

    /// <summary>解码后的候选框（letterbox 坐标系，单位像素）</summary>
    private readonly record struct Candidate(int ClassId, float Score, float Cx, float Cy, float W, float H);

    /// <summary>
    /// 解析模型输出 [1, attrs, anchors]
    ///
    /// 两种排布都要兼容：ultralytics 导出通常是 [1,10,8400]，
    /// 但经过某些转换工具（如 onnx-simplifier 之外的第三方图改写）可能变成 [1,8400,10]。
    /// 这里按维度大小自动判断，避免换个导出参数就崩。
    /// </summary>
    private List<Candidate> ExtractCandidates(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        var dense = results[0].AsTensor<float>().ToDenseTensor();
        var dims = dense.Dimensions.ToArray();

        if (dims.Length != 3)
            throw new InvalidOperationException(
                $"模型输出维度异常：期望 3 维，实际 [{string.Join(",", dims)}]");

        int d1 = dims[1], d2 = dims[2];
        bool transposed = d1 > d2;             // true: [1, anchors, attrs]
        int attrs = transposed ? d2 : d1;
        int anchors = transposed ? d1 : d2;

        // 有没有 objectness 通道：v8 以后没有，但留个自动判断，兼容别人给的 v5 模型
        int objectnessOffset = attrs - ClassNames.Length - 4;
        if (objectnessOffset < 0)
            throw new InvalidOperationException(
                $"模型输出通道数 {attrs} 与类别数 {ClassNames.Length} 不匹配（至少需要 {4 + ClassNames.Length}）");

        var buffer = dense.Buffer.Span;
        var list = new List<Candidate>(256);

        for (int i = 0; i < anchors; i++)
        {
            // 定位第 i 个候选的 4 个框参数
            int baseIdx = transposed ? i * attrs : i;
            int step = transposed ? 1 : anchors;

            float cx = buffer[baseIdx];
            float cy = buffer[baseIdx + step];
            float w = buffer[baseIdx + 2 * step];
            float h = buffer[baseIdx + 3 * step];

            // 取类别分数最大值（无 objectness，分数即可信度）
            int clsBase = 4 + objectnessOffset;
            float best = 0f;
            int bestCls = -1;
            for (int c = 0; c < ClassNames.Length; c++)
            {
                float s = buffer[baseIdx + (clsBase + c) * step];
                if (s > best) { best = s; bestCls = c; }
            }

            if (bestCls < 0 || best < ConfThreshold) continue;
            list.Add(new Candidate(bestCls, best, cx, cy, w, h));
        }

        return list;
    }

    /// <summary>
    /// 置信度过滤后按类别做 NMS。
    ///
    /// 为什么是"按类别"而不是全局 NMS？
    ///   一块区域可能同时被判成 patches 和 rolled-in_scale（分数接近），
    ///   全局 NMS 会把分低的那个直接抹掉，而现场恰恰想知道"这块可能是什么"。
    ///   按类别分开做，两类都能保留，最终由工艺规则决定判据。
    /// </summary>
    private List<Candidate> FilterAndNms(List<Candidate> candidates)
    {
        if (candidates.Count == 0) return candidates;

        var kept = new List<Candidate>(candidates.Count);

        foreach (var group in candidates.GroupBy(c => c.ClassId))
        {
            var items = group.ToList();
            var boxes = items
                .Select(c => new Rect(
                    (int)MathF.Round(c.Cx - c.W / 2),
                    (int)MathF.Round(c.Cy - c.H / 2),
                    Math.Max(1, (int)MathF.Round(c.W)),
                    Math.Max(1, (int)MathF.Round(c.H))))
                .ToArray();
            var scores = items.Select(c => c.Score).ToArray();

            // OpenCvSharp 自带 NMS（在 Dnn 命名空间），不用自己写 IoU 循环
            CvDnn.NMSBoxes(boxes, scores, ConfThreshold, NmsThreshold, out int[] idx);
            foreach (int i in idx)
                kept.Add(items[i]);
        }

        // 按分数降序，UI 列表和超限裁剪都用这个顺序
        kept.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (kept.Count > MaxDetections)
            kept.RemoveRange(MaxDetections, kept.Count - MaxDetections);

        return kept;
    }

    /// <summary>释放推理会话（应用退出时调用；Pipeline 不负责释放工具）</summary>
    public void Dispose()
    {
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
        }
    }
}
