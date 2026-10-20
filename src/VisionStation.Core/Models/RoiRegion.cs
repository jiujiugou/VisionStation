using OpenCvSharp;

namespace VisionStation.Core.Models;

/// <summary>
/// 感兴趣区域 —— 图像上的一块矩形子区域
///
/// 坐标约定：
///   - X/Y/Width/Height 都是图像像素坐标（单位：px）
///   - (0,0) = 图像左上角
///
/// 用途：
///   - 检测算法的输入（每个 Tool 在自己的 ROI 区域内运行）
///   - 渲染标注的参考（DrawAnnotations 根据它画 ROI 框）
///
/// 生命周期：
///   - 从配方文件 deserialize → InspectionWorkflow 取快照 → Pipeline 传入各 Tool
/// </summary>
public class RoiRegion
{
    /// <summary>ROI 名称（与工具名称对应，如 "Presence"）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>左上角 X 像素坐标</summary>
    public int X { get; set; }

    /// <summary>左上角 Y 像素坐标</summary>
    public int Y { get; set; }

    /// <summary>宽度（像素）</summary>
    public int Width { get; set; }

    /// <summary>高度（像素）</summary>
    public int Height { get; set; }

    /// <summary>
    /// 从原图裁切出 ROI 子图。
    /// 注意：返回的 Mat 是原图的视图（零拷贝），不是独立副本。
    /// 不要释放返回的 Mat 后继续使用原图——可能会造成数据不一致。
    /// </summary>
    /// <param name="source">原始图像</param>
    /// <returns>ROI 子图（视图，非副本）</returns>
    public Mat Crop(Mat source)
    {
        var rect = new Rect(X, Y, Width, Height);
        return new Mat(source, rect);
    }

    /// <summary>转换为 OpenCV Rect 结构（用于 Rectangle 绘制等）</summary>
    public Rect ToRect() => new(X, Y, Width, Height);
}
