using OpenCvSharp;
using VisionStation.Core.Models;

namespace VisionStation.Core.Tools;

/// <summary>
/// 视觉检测工具统一接口 —— VisionStation 最重要的架构抽象
///
/// 为什么需要这个接口？
///   之前三个检测器（PresenceDetector / DimensionDetector / DefectDetector）各自为政，
///   Pipeline 需要硬编码调用每个工具的方法。加新工具（如 OCR、二维码、边缘检测）就要改 Pipeline。
///
///   引入此接口后：Pipeline 只知道 IVisionTool，不知道任何具体工具。
///   加新检测功能只需写一个类实现此接口，注册到工具列表，Pipeline 一行不改。
///
/// 设计决策：
///   - 参数在构造函数注入（不在 Execute 参数里）—— 工具创建时就知道自己用什么阈值
///   - Execute 只收图像和 ROI —— 调用方只关心"在哪张图的哪块区域"执行
///   - 返回 ToolResult（而非老 DetectionResult）—— 统一了所有工具的产出格式
///
/// 每个实现类都是一个完整的检测工具，包含了：
///   1. 检测算法（模板匹配 / 霍夫圆 / Blob 分析）
///   2. 参数阈值（匹配度 / 公差 / 最小面积）
///   3. 标注指令（DrawCommands：告诉渲染层画什么）
/// </summary>
public interface IVisionTool
{
    /// <summary>
    /// 工具名称。
    /// 必须与 ROI 的 Name 对应（如 "Presence" 工具匹配 "Presence" ROI）。
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 执行检测 —— 整个工具链的原子单位
    ///
    /// 调用约定：
    ///   - grayImage：已预处理好的灰度图（由 Pipeline 统一转，工具不用自己转）
    ///   - roi：本工具的检测区域（坐标已经是图像像素坐标）
    ///   - 返回 ToolResult：包含判定、摘要、指标、标注指令
    ///   - 异常由 Pipeline 捕获，工具内部不需要 try-catch
    /// </summary>
    /// <param name="grayImage">原图的灰度版本（Pipeline 已 CvtColor BGR2GRAY）</param>
    /// <param name="roi">此工具的感兴趣区域</param>
    /// <returns>统一的检测结果</returns>
    ToolResult Execute(Mat grayImage, RoiRegion roi);
}
