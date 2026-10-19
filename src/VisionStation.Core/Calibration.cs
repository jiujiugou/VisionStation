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
    /// <exception cref="ArgumentOutOfRangeException">标定比例非有限正数时</exception>
    public double PixelToMm(double pixels)
    {
        EnsureScale();
        return pixels / _config.PixelPerMillimeter;
    }

    /// <summary>毫米 → 像素距离</summary>
    /// <param name="mm">毫米数</param>
    /// <returns>对应的像素数</returns>
    /// <exception cref="ArgumentOutOfRangeException">标定比例非有限正数时</exception>
    public double MmToPixel(double mm)
    {
        EnsureScale();
        return mm * _config.PixelPerMillimeter;
    }

    /// <summary>
    /// 标定比例守卫（F-C04）。
    /// 比例为 0 时除法得 ∞，下游 DimensionTool 会把 ∞ 当实测直径，全线判超差；
    /// 负值/NaN 同样产出无意义的测量值。这种配置错误必须当场报错，
    /// 不能让现场去猜"是标定填错了还是零件真超差"。
    /// </summary>
    private void EnsureScale()
    {
        double v = _config.PixelPerMillimeter;
        if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(CalibrationConfig.PixelPerMillimeter), v,
                "标定比例 PixelPerMillimeter 必须是有限正数（px/mm）");
    }

    /// <summary>检查实测值是否在设计公差的范围内</summary>
    /// <param name="measuredMm">实测毫米值</param>
    /// <returns>偏差 ≤ Tolerance 返回 true</returns>
    public bool IsWithinTolerance(double measuredMm)
        => Math.Abs(measuredMm - _config.DesignedDiameterMm) <= _config.DimensionToleranceMm;
}
