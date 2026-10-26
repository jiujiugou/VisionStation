using System.IO;

namespace VisionStation.Core.Models;

/// <summary>
/// 一份完整的检测配方 —— 检测一个产品所需的全部配置。
///
/// schemaVersion 2（2026-10-23）：从"自研工具链"改成"引擎 + 引擎参数"。
///   · 删掉了 rois / tools / calibration 三段 —— ROI 与标定归引擎所有
///     （VM 在 .sol 里配，ONNX 不需要），留在配方里只会造出第二份真相。
///   · 新增 engine 段与 vm / onnx 两段参数。
///
/// 为什么引擎写在配方里而不是 appsettings：
///   .sol 流程文件与模型文件都是**跟产品绑定**的（换产品就换流程），
///   而 appsettings 描述的是**这台机器**（相机、PLC、存储路径）。
///   两者改动的时机完全不同，混在一起就会出现"换配方还要重启改配置"。
///
/// 序列化：System.Text.Json。换产品 = 换一个 json，代码不动。
/// </summary>
public class RecipeConfig
{
    /// <summary>当前配方结构版本（改字段含义时靠它做迁移，不是软件版本号）</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>配方名称，如 "金属垫圈检测"</summary>
    public string Name { get; set; } = "默认配方";

    /// <summary>配方结构版本</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>最近一次保存时间</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// 本配方使用的检测引擎。大小写不敏感。
    ///
    /// 默认 <c>Vm</c>：运行时只跑 VM（ONNX 推理已移出运行时，归 tools/GoldenSetRunner）。
    /// 默认值写错会让一份漏填 engine 的配方悄悄走到一条不存在的路上，
    /// 所以这里必须和运行时的实际能力一致。
    /// </summary>
    public string Engine { get; set; } = EngineNames.Vm;

    /// <summary>
    /// 本配方适配的画面尺寸。
    ///
    /// 为什么必须有：ROI 与标定都死死绑在某个分辨率上。换相机（1280 → 4024）
    /// 而配方没跟着改时，流程里的 ROI 会框到零件外面、尺寸会算错，
    /// 但程序**一切看起来正常**——不报错、不崩、只是安静地判错。
    /// 未声明（Width/Height 为 0）视为旧配方，不校验。
    /// </summary>
    public SensorSpec Sensor { get; set; } = new();

    /// <summary>VisionMaster 流程参数（Engine = Vm 时生效）</summary>
    public VmRecipeData Vm { get; set; } = new();

    /// <summary>ONNX 模型参数（Engine = Onnx 时生效）</summary>
    public OnnxRecipeData Onnx { get; set; } = new();

    /// <summary>
    /// 校验本配方能否用于当前画面。返回 null = 通过；否则返回可直接展示给操作员的原因。
    /// 纯函数、无 IO，便于单测。
    /// </summary>
    public string? CheckSensor(int frameWidth, int frameHeight)
    {
        if (!Sensor.IsDeclared) return null;
        if (Sensor.Width == frameWidth && Sensor.Height == frameHeight) return null;

        return $"配方「{Name}」按 {Sensor.Width}×{Sensor.Height} 制作，"
             + $"当前画面 {frameWidth}×{frameHeight}；流程 ROI 与标定都会错位，已拒绝本帧检测";
    }

    /// <summary>
    /// 配方自身的合法性（与画面无关）。返回值是要给人看的问题清单，空 = 合法。
    /// 启动自检用它，避免"参数填错 → 第一帧才炸，而且炸得很隐晦"。
    /// </summary>
    public List<string> Validate()
    {
        var problems = new List<string>();

        if (!EngineNames.All.Contains(Engine, StringComparer.OrdinalIgnoreCase))
            problems.Add($"未知引擎 \"{Engine}\"，可选值：{string.Join(" / ", EngineNames.All)}");

        if (string.Equals(Engine, EngineNames.Vm, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(Vm.SolutionPath))
                problems.Add("Vm 引擎未指定流程文件（vm.solution）");
            if (string.IsNullOrWhiteSpace(Vm.Procedure))
                problems.Add("Vm 引擎未指定流程名（vm.procedure）");
            if (string.IsNullOrWhiteSpace(Vm.ImageSourceModule))
                problems.Add("Vm 引擎未指定图像源模块名（vm.imageSourceModule）");
            if (Vm.Outputs.Count == 0)
                problems.Add("Vm 引擎未配置任何输出映射（vm.outputs），检测将没有任何结果项");

            var duplicated = Vm.Outputs
                .GroupBy(o => o.Label, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);
            foreach (var label in duplicated)
                problems.Add($"Vm 输出 label 重复：{label}（会互相覆盖）");

            foreach (var o in Vm.Outputs.Where(o => string.IsNullOrWhiteSpace(o.Module) || string.IsNullOrWhiteSpace(o.Label)))
                problems.Add("Vm 输出映射缺少 module 或 label");
        }
        else if (string.Equals(Engine, EngineNames.Onnx, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(Onnx.ModelPath))
                problems.Add("Onnx 引擎未指定模型路径（onnx.model）");
            if (Onnx.Confidence is <= 0 or > 1)
                problems.Add($"onnx.conf = {Onnx.Confidence} 不合法，必须在 (0,1]");
            if (Onnx.Nms is <= 0 or > 1)
                problems.Add($"onnx.nms = {Onnx.Nms} 不合法，必须在 (0,1]");
            if (Onnx.Classes.Length == 0)
                problems.Add("onnx.classes 为空，无法解码任何检测框");
        }

        return problems;
    }

    /// <summary>
    /// 判断一段 JSON 是不是配方。
    ///
    /// 判据是"有没有显式声明 engine" —— 不是"有没有 tools"（那个字段已经不存在了），
    /// 也不是"能不能反序列化"：任何 JSON 都能反序列化成一个字段全默认的对象，
    /// 那样等于把 config 目录里任何一个 json 都当配方。
    /// 语义上这也是对的：一份说不出自己用哪个引擎的配方，本来就不该被加载。
    /// </summary>
    public static bool HasEngineDeclaration(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return false;

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, nameof(Engine), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 把配方里的相对路径解析成绝对路径。
    ///
    /// 相对路径的基准是 <paramref name="assetRoot"/>（组合根传 exe 目录，与 AppPaths 一致），
    /// **不用 Environment.CurrentDirectory** —— 快捷方式、计划任务、调试器给的 cwd 各不相同，
    /// 基于它找文件必然时灵时不灵。
    /// </summary>
    public static string ResolveAsset(string configured, string assetRoot)
    {
        if (string.IsNullOrWhiteSpace(configured)) return "";
        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(assetRoot, configured));
    }
}

/// <summary>引擎名常量 —— 与配方 json 里的字符串一一对应，避免各处手写</summary>
public static class EngineNames
{
    /// <summary>海康 VisionMaster 流程</summary>
    public const string Vm = "Vm";

    /// <summary>ONNX YOLO 缺陷检测</summary>
    public const string Onnx = "Onnx";

    /// <summary>合法引擎集合</summary>
    public static readonly string[] All = { Vm, Onnx };
}

/// <summary>配方绑定的画面尺寸（像素）</summary>
public class SensorSpec
{
    /// <summary>画面宽度（px）</summary>
    public int Width { get; set; }

    /// <summary>画面高度（px）</summary>
    public int Height { get; set; }

    /// <summary>是否声明过尺寸。旧配方没有这一段，值为 0，此时不参与校验。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDeclared => Width > 0 && Height > 0;
}

/// <summary>VisionMaster 流程参数</summary>
public class VmRecipeData
{
    /// <summary>流程文件（.sol）路径。相对路径按 exe 目录解析。</summary>
    public string SolutionPath { get; set; } = "";

    /// <summary>方案密码（无密码留空）</summary>
    public string Password { get; set; } = "";

    /// <summary>要运行的流程名，如 "流程1"</summary>
    public string Procedure { get; set; } = "流程1";

    /// <summary>流程里图像源模块的名字 —— C# 通过它把帧喂进去</summary>
    public string ImageSourceModule { get; set; } = "图像源1";

    /// <summary>
    /// 输出映射表：.sol 里的模块 → 报告里的结果项。
    ///
    /// 为什么要有它：模块名和输出名是工程师在平台里起的，代码不可能预先知道。
    /// 把映射放进配方，换 .sol 只改 json，不用重新编译 ——
    /// 这也是"平台 + 二次开发"里 C# 唯一必须维护的东西。
    /// </summary>
    public List<VmOutputMapping> Outputs { get; set; } = new();
}

/// <summary>一条 VM 输出映射：一个模块的一项输出 → 报告里的一项结果</summary>
public class VmOutputMapping
{
    /// <summary>.sol 里的模块名（如 "圆查找1"）</summary>
    public string Module { get; set; } = "";

    /// <summary>报告里的业务名，也是 Results 的键（如 "外径"）</summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// 结果角色，可选：Locate / Measure / Defect。
    /// 只影响历史记录的旧列投影（见 ResultKeys），留空则仅进 JSON 快照。
    /// </summary>
    public string Role { get; set; } = "";

    /// <summary>该模块的输出名（如 "Radius"）。留空 = 只看模块判定，不取数值。</summary>
    public string Io { get; set; } = "";

    /// <summary>
    /// 输出类型：<c>Float</c>（默认，取浮点数组首个值）或 <c>Circle</c>（取圆半径）。
    /// 需要别的类型时在这里扩展，不要在引擎里加特判。
    /// </summary>
    public string IoKind { get; set; } = "Float";

    /// <summary>写进 Metrics 的键名，留空用 Label</summary>
    public string Metric { get; set; } = "";

    /// <summary>物理单位，仅用于摘要显示（如 "mm"）</summary>
    public string Unit { get; set; } = "";
}

/// <summary>ONNX YOLO 参数</summary>
public class OnnxRecipeData
{
    /// <summary>模型路径。相对路径按 exe 目录解析。</summary>
    public string ModelPath { get; set; } = @"models\neu-det.onnx";

    /// <summary>置信度阈值（低于此分数的候选框直接丢弃）</summary>
    public double Confidence { get; set; } = 0.25;

    /// <summary>NMS 的 IoU 阈值</summary>
    public double Nms { get; set; } = 0.45;

    /// <summary>单帧最大保留框数</summary>
    public int MaxDetections { get; set; } = 100;

    /// <summary>
    /// 类别名，顺序必须与训练时 data.yaml 的 names 完全一致。
    /// 默认是 NEU-DET 的 6 类（当前权重就是这么训的）。
    /// </summary>
    public string[] Classes { get; set; } =
    {
        "crazing", "inclusion", "patches",
        "pitted_surface", "rolled-in_scale", "scratches"
    };
}
