using VisionStation.Core.Models;

namespace VisionStation.Core;

/// <summary>
/// 简易标定 —— 图像像素 ↔ 物理毫米 双向换算
///
/// V1 方案：手动填入 PixelPerMillimeter（一张已知尺寸的零件拍一张，量像素÷毫米 得到比例）。
/// V2 方案：棋盘格标定（张正友标定法），计算内参矩阵和畸变系数。
///
/// 为什么需要这个类：
///   尺寸检测只看像素值没意义——"孔直径 40 像素"操作员看不懂。
///   需要换算成毫米："孔直径 2.67mm"才是产线要的数据。
///   这个类把 px↔mm 的换算逻辑单独封装，所有需要换算的地方共用。
///
/// 使用：
///   calib.SetConfig(new CalibrationConfig { PixelPerMillimeter = 15.0 });
///   double mm = calib.PixelToMm(60);  // 60px → 4mm
/// </summary>
public class Calibration
{
    private CalibrationConfig _config = new();

    /// <summary>更新标定参数（换产品时调用）</summary>
    public void SetConfig(CalibrationConfig config) => _config = config;

    /// <summary>像素距离 → 毫米</summary>
    /// <param name="pixels">像素数</param>
    /// <returns>对应的毫米数</returns>
    public double PixelToMm(double pixels) => pixels / _config.PixelPerMillimeter;

    /// <summary>毫米 → 像素距离</summary>
    /// <param name="mm">毫米数</param>
    /// <returns>对应的像素数</returns>
    public double MmToPixel(double mm) => mm * _config.PixelPerMillimeter;

    /// <summary>检查实测值是否在设计公差的范围内</summary>
    /// <param name="measuredMm">实测毫米值</param>
    /// <returns>偏差 ≤ Tolerance 返回 true</returns>
    public bool IsWithinTolerance(double measuredMm)
        => Math.Abs(measuredMm - _config.DesignedDiameterMm) <= _config.DimensionToleranceMm;
}
