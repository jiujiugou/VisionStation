using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace VisionStation.Engines.Onnx;

/// <summary>一个检测框（原图坐标系，单位像素）</summary>
/// <param name="ClassId">类别下标</param>
/// <param name="ClassName">类别名（来自配方的 classes，顺序与训练一致）</param>
/// <param name="Score">置信度</param>
/// <param name="Box">原图上的矩形</param>
public readonly record struct Detection(int ClassId, string ClassName, float Score, Rect Box);

/// <summary>
/// YOLO 推理器 —— 只做"图进、框出"，不含判定、不含报告、不碰界面。
///
/// 为什么和引擎分成两个类：
///   推理是纯算法（可单独对着黄金样本跑批），判定与报告是业务（换引擎就要换）。
///   混在一起的话，想对 105 张样本单独跑推理就得先把整条业务链路装起来。
///
/// 模型信息（NEU-DET 钢材表面缺陷，6 类）：
///   输入  1 x 3 x 640 x 640  float32，RGB，/255 归一化，HWC -&gt; CHW
///   输出  1 x 10 x 8400      每列 = [cx, cy, w, h, cls0..cls5]
///   注意  YOLOv8 起**没有 objectness 通道**，置信度直接取类别分数的最大值。
///         这一点和 YOLOv5 不同，移植代码时最容易在这里写错。
///
/// 线程约定：
///   一个实例只在一个线程上跑（检测链路本来就是串行的）。
///   推理会话惰性创建 —— 构造要解析图 + 算子融合（100~300ms），不能每帧新建。
/// </summary>
public sealed class YoloDetector : IDisposable
{
    /// <summary>模型输入边长（训练时 imgsz=640，导出时 fixed shape，改模型才改这里）</summary>
    private const int InputSize = 640;

    /// <summary>letterbox 填充灰度值 —— 114 是 ultralytics 的固定约定，不要改，改了精度会掉</summary>
    private const float PadValue = 114f;

    private readonly string _modelPath;
    private readonly float _confThreshold;
    private readonly float _nmsThreshold;
    private readonly int _maxDetections;

    private readonly object _sessionLock = new();
    private InferenceSession? _session;

    /// <param name="modelPath">ONNX 模型绝对路径</param>
    /// <param name="confThreshold">置信度阈值，必须在 (0,1]</param>
    /// <param name="nmsThreshold">NMS IoU 阈值，必须在 (0,1]</param>
    /// <param name="classNames">类别名，不能为空</param>
    /// <param name="maxDetections">单帧最大保留框数，必须 ≥ 1</param>
    public YoloDetector(string modelPath,
        float confThreshold,
        float nmsThreshold,
        IReadOnlyList<string> classNames,
        int maxDetections)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("模型路径不能为空", nameof(modelPath));

        // 越界必须立刻失败（F-C09）：阈值 > 1 会让所有候选被过滤掉，
        // 结果返回"未检出缺陷"= OK —— 模型什么都没判，系统却报合格。
        if (float.IsNaN(confThreshold) || confThreshold <= 0f || confThreshold > 1f)
            throw new ArgumentOutOfRangeException(nameof(confThreshold), confThreshold,
                "置信度阈值必须在 (0,1] 内；填 1.5 之类会让所有检测被静默丢弃");

        if (float.IsNaN(nmsThreshold) || nmsThreshold <= 0f || nmsThreshold > 1f)
            throw new ArgumentOutOfRangeException(nameof(nmsThreshold), nmsThreshold,
                "NMS 阈值必须在 (0,1] 内");

        if (classNames is null || classNames.Count == 0)
            throw new ArgumentException("类别名不能为空：没有类别名就无法解码任何候选框", nameof(classNames));

        if (maxDetections < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDetections), maxDetections,
                "单帧最大保留框数必须 ≥ 1；填 0 会让所有检测结果被裁掉并报合格");

        _modelPath = modelPath;
        _confThreshold = confThreshold;
        _nmsThreshold = nmsThreshold;
        ClassNames = classNames.ToArray();
        _maxDetections = maxDetections;
    }

    /// <summary>类别名（顺序与训练时 data.yaml 一致）</summary>
    public string[] ClassNames { get; }

    /// <summary>是否已完成模型加载（界面可用来提示"模型未就绪"）</summary>
    public bool IsLoaded
    {
        get { lock (_sessionLock) return _session != null; }
    }

    /// <summary>
    /// 对整幅图做检测。
    ///
    /// 处理流程：
    ///   1. 统一转 RGB（灰度图 GRAY-&gt;RGB，三通道相同，语义等价，不引入失真）
    ///   2. letterbox 缩放：等比缩放到 640 内，右下角补 114 灰边
    ///      —— 不能直接 Resize，直接拉伸会改变缺陷长宽比，模型没见过这种形变
    ///   3. 填进 DenseTensor：/255 归一化 + HWC -&gt; CHW
    ///   4. 推理 → 解 [1,10,8400]
    ///   5. 置信度过滤 → 按类别分别 NMS
    ///   6. 坐标反算：letterbox → 原图
    /// </summary>
    /// <param name="image">原始图像（1/3/4 通道均可），所有权归调用方</param>
    /// <param name="inferenceMs">推理耗时（毫秒），不含预处理</param>
    public IReadOnlyList<Detection> Detect(Mat image, out double inferenceMs)
    {
        ArgumentNullException.ThrowIfNull(image);
        inferenceMs = 0;

        if (image.Empty())
            throw new ArgumentException("图像为空，无法检测", nameof(image));

        using var rgb = ToRgb(image);

        // ── letterbox 到 640 ──
        var (padded, scale, padX, padY) = Letterbox(rgb);

        // ── 组装输入张量 ──
        var input = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        FillTensor(padded, input);
        padded.Dispose();

        // ── 推理 ──
        var session = EnsureSession();
        var inputName = session.InputMetadata.Keys.First();
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, input) };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var results = session.Run(inputs);
        sw.Stop();
        inferenceMs = sw.Elapsed.TotalMilliseconds;

        // ── 解码 + 过滤 + NMS ──
        var raw = ExtractCandidates(results);
        var kept = FilterAndNms(raw);

        // ── 坐标反算回原图 ──
        var detections = new List<Detection>(kept.Count);
        foreach (var det in kept)
        {
            // letterbox 坐标 → 原图坐标：先减填充、再除缩放比
            float x1 = (det.Cx - det.W / 2 - padX) / scale;
            float y1 = (det.Cy - det.H / 2 - padY) / scale;
            float w = det.W / scale;
            float h = det.H / scale;

            // 裁到图像边界内（letterbox 反算可能溢出几个像素）
            int left = Math.Max(0, (int)MathF.Round(x1));
            int top = Math.Max(0, (int)MathF.Round(y1));
            int right = Math.Min(image.Width, (int)MathF.Round(x1 + w));
            int bottom = Math.Min(image.Height, (int)MathF.Round(y1 + h));
            if (right <= left || bottom <= top) continue;

            string clsName = det.ClassId < ClassNames.Length
                ? ClassNames[det.ClassId]
                : $"cls{det.ClassId}";

            detections.Add(new Detection(det.ClassId, clsName, det.Score,
                new Rect(left, top, right - left, bottom - top)));
        }

        return detections;
    }

    /// <summary>统一转成 3 通道 RGB（YOLO 训练用的是 RGB）</summary>
    private static Mat ToRgb(Mat image)
    {
        var rgb = new Mat();
        switch (image.Channels())
        {
            case 1:
                Cv2.CvtColor(image, rgb, ColorConversionCodes.GRAY2RGB);
                break;
            case 4:
                Cv2.CvtColor(image, rgb, ColorConversionCodes.BGRA2RGB);
                break;
            default:
                Cv2.CvtColor(image, rgb, ColorConversionCodes.BGR2RGB);
                break;
        }

        return rgb;
    }

    /// <summary>
    /// 惰性创建推理会话 —— 第一次检测时才真正加载模型，
    /// 避免应用启动时因为一个没配置的模型路径就整个崩掉。
    /// </summary>
    private InferenceSession EnsureSession()
    {
        if (_session != null) return _session;

        lock (_sessionLock)
        {
            if (_session != null) return _session;

            if (!File.Exists(_modelPath))
                throw new FileNotFoundException($"YOLO 模型文件不存在：{_modelPath}");

            var options = new SessionOptions
            {
                // ORT_ENABLE_ALL：启用算子融合 + 常量折叠，CPU 上通常能快 20~30%
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            // 线程数取一半核心：工业现场 CPU 还要留给采集线程和 UI，不能全占
            options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);

            _session = new InferenceSession(_modelPath, options);
            return _session;
        }
    }

    /// <summary>
    /// letterbox 等比缩放 —— YOLO 的标准预处理。
    ///
    /// 为什么不能直接 Resize(640,640)？
    ///   假设画面是 800x200 的细长条，硬拉伸成正方形会把缺陷横向压扁 4 倍，
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
    /// 解析模型输出 [1, attrs, anchors]。
    ///
    /// 两种排布都要兼容：ultralytics 导出通常是 [1,10,8400]，
    /// 但经过某些转换工具（如第三方图改写）可能变成 [1,8400,10]。
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

            if (bestCls < 0 || best < _confThreshold) continue;
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
            CvDnn.NMSBoxes(boxes, scores, _confThreshold, _nmsThreshold, out int[] idx);
            foreach (int i in idx)
                kept.Add(items[i]);
        }

        // 按分数降序，UI 列表和超限裁剪都用这个顺序
        kept.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (kept.Count > _maxDetections)
            kept.RemoveRange(_maxDetections, kept.Count - _maxDetections);

        return kept;
    }

    /// <summary>释放推理会话（引擎退出时调用）</summary>
    public void Dispose()
    {
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
        }
    }
}
