namespace VisionStation.Core.Models;

/// <summary>
/// 一份完整的检测配方 —— 包含检测一个产品所需的所有信息
///
/// 为什么需要这个类？
///   之前 ROIs 存在 rois.json、Tool 参数硬编码在代码、标定参数散在 appsettings.json。
///   换一个产品要改三个地方。现在所有东西收进一个配方文件：
///     Config/recipes/default.json → RecipeConfig → ROIs + Tools + Calibration
///   换产品 = 加载不同的配方 JSON，代码不动。
///
/// 序列化：JsonSerializer.Serialize / Deserialize
/// </summary>
public class RecipeConfig
{
    /// <summary>配方名称，如 "金属垫圈检测"</summary>
    public string Name { get; set; } = "默认配方";

    /// <summary>配方结构版本（将来改字段含义时靠它做迁移，不是软件版本号）</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>最近一次保存时间（编辑器保存时自动刷新，用于判断"这份配方多久没动过"）</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// 本配方适配的画面尺寸。
    ///
    /// 为什么必须有：ROI 是像素坐标、标定是 px/mm，两者都死死绑在某个分辨率上。
    /// 换相机（1280 → 2448）而配方没跟着改时，ROI 会框到零件外面、尺寸会算错，
    /// 但程序**一切看起来正常**——不报错、不崩、只是安静地判错。
    /// 未声明（Width/Height 为 0）视为旧配方，不校验，避免把存量配方判死。
    /// </summary>
    public SensorSpec Sensor { get; set; } = new();

    /// <summary>
    /// 校验本配方能否用于当前画面。返回 null = 通过；否则返回可直接展示给操作员的原因。
    /// 纯函数、无 IO，便于单测。
    /// </summary>
    public string? CheckSensor(int frameWidth, int frameHeight)
    {
        if (!Sensor.IsDeclared) return null;
        if (Sensor.Width == frameWidth && Sensor.Height == frameHeight) return null;

        return $"配方「{Name}」按 {Sensor.Width}×{Sensor.Height} 制作，"
             + $"当前画面 {frameWidth}×{frameHeight}；ROI 与标定都会错位，已拒绝本帧检测";
    }

    /// <summary>标定参数（px↔mm 换算 + 设计直径 + 公差）</summary>
    public CalibrationData Calibration { get; set; } = new();

    /// <summary>ROI 区域列表</summary>
    public List<RoiData> Rois { get; set; } = new();

    /// <summary>工具配置列表（按此顺序执行）</summary>
    public List<ToolConfigData> Tools { get; set; } = new();
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

/// <summary>标定数据（配方文件中的 calibration 段）</summary>
public class CalibrationData
{
    /// <summary>1 毫米 = 多少像素（如 15.0 表示 1mm = 15px）</summary>
    public double PixelPerMillimeter { get; set; } = 15.0;

    /// <summary>设计直径（毫米），用于尺寸对比</summary>
    public double DesignedDiameterMm { get; set; } = 2.67;

    /// <summary>允许偏差（毫米），超出偏差范围判 NG</summary>
    public double DimensionToleranceMm { get; set; } = 0.50;
}

/// <summary>单个 ROI 数据（配方文件中的 rois 段的一个元素）</summary>
public class RoiData
{
    /// <summary>ROI 名称（与 Tool.Name 匹配，如 "Presence"）</summary>
    public string Name { get; set; } = "";

    /// <summary>左上角 X（像素）</summary>
    public int X { get; set; }

    /// <summary>左上角 Y（像素）</summary>
    public int Y { get; set; }

    /// <summary>宽度（像素）</summary>
    public int Width { get; set; }

    /// <summary>高度（像素）</summary>
    public int Height { get; set; }
}

/// <summary>单个工具配置（配方文件中的 tools 段的一个元素）</summary>
public class ToolConfigData
{
    /// <summary>工具类型字符串：Presence / Dimension / Defect / OCR / ...</summary>
    public string Type { get; set; } = "";

    /// <summary>是否启用（false = 跳过此工具）</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>关联的 ROI 名称（与 RoiData.Name 匹配）</summary>
    public string Roi { get; set; } = "";

    /// <summary>
    /// 模型文件路径（仅深度学习类工具用，如 Yolo）。
    /// 留空 = 走约定路径 models/neu-det.onnx（见 ToolFactory.ResolveModelPath）。
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// 模板图路径（仅 Presence 用）。
    /// 留空 = 用 appsettings 的 Detection:TemplatePath（全局默认）。
    /// 为什么要能覆盖：模板和 ROI、标定一样绑在分辨率上，两份配方（1280 / 2448）
    /// 必须能各带各的模板，否则多配方在"模板"这一项上就是假的。
    /// </summary>
    public string Template { get; set; } = "";

    /// <summary>工具参数（如 {"threshold": 0.70, "areaMin": 50}）</summary>
    public Dictionary<string, double> Params { get; set; } = new();
}
