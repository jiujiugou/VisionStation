namespace VisionStation.Core.Models;

/// <summary>
/// 标定配置 —— 运行时一次性设定，全局共享
///
/// 来源：配方文件 calibration 段 → App.xaml.cs 创建此实例 → DI 注入为单例
///
/// 为什么是独立的 Config 而不是直接读 RecipeConfig.Calibration？
///   Calibration 类本身不依赖 RecipeConfig——只依赖这个纯配置，
///   解耦后 Calibration 可以在不同场景（标定工具、调试面板）复用。
/// </summary>
public class CalibrationConfig
{
    /// <summary>像素/毫米 比例（如 15.0 表示 1mm = 15px）</summary>
    public double PixelPerMillimeter { get; set; } = 15.82;

    /// <summary>设计直径（毫米）—— 尺寸检测的参考值</summary>
    public double DesignedDiameterMm { get; set; } = 10.0;

    /// <summary>允许的直径偏差（毫米）—— 超限判 NG</summary>
    public double DimensionToleranceMm { get; set; } = 0.10;

    /// <summary>像素距离 → 毫米</summary>
    public double PixelToMm(double pixels) => pixels / PixelPerMillimeter;

    /// <summary>毫米 → 像素距离</summary>
    public double MmToPixel(double mm) => mm * PixelPerMillimeter;
}
