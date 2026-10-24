using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace VisionStation.App.Services.Imaging;

/// <summary>
/// OpenCV <see cref="Mat"/> → WPF <see cref="BitmapSource"/>，只在 App 层出现。
///
/// 为什么是静态 helper 而不是接口：它只做一件事、没有第二个实现、也不跨进程。
/// 抽成 <c>IBitmapAdapter</c> 只是为了在测试里换掉它，而它真正需要被验证的
/// 只有一条——**返回的位图必须是冻结的**，直接断言 <c>IsFrozen</c> 就够了。
///
/// 为什么必须 Freeze：<see cref="BitmapSource"/> 默认带 Dispatcher 亲和性，
/// 在后台线程创建、由 UI 线程绑定到 Image 控件会抛
/// "该对象归另一个线程所有"。S4 之后位图是在检测线程上创建的，
/// 不冻结就必然踩这一条。冻结之后位图不可再改，所以每次转换都要新建。
/// </summary>
public static class MatBitmap
{
    /// <summary>
    /// 转成已冻结的 BGR24 位图。返回 null 表示输入为空（调用方按"没有图"处理）。
    /// </summary>
    /// <param name="mat">源图</param>
    /// <param name="maxWidth">
    /// 上屏最大宽度；0 = 不缩放。
    ///
    /// 为什么需要：界面上显示区不到 900px 宽，而图是 2448 宽的。
    /// 不缩放的话每帧要做两次 `new byte[15MB] + Marshal.Copy`（原图 + 标注图），
    /// 5 件/秒就是 150MB/s 的纯拷贝，全落在大对象堆上，Gen2 GC 会明显卡顿。
    /// 缩到 1280 之后同样的两帧只要 7.5MB，拷贝和分配都降 75%。
    ///
    /// 用 <see cref="InterpolationFlags.Area"/> 而不是默认的双线性：缩小时 Area 是像素平均，
    /// 更接近"肉眼看缩略图"的效果，也更快。
    /// </param>
    /// <remarks>
    /// 单通道（黑白工业相机直出 8UC1）和四通道都会先转成三通道：
    /// WPF 的 <c>Bgr24</c> 要求 3 字节/像素，通道数不匹配时 <c>BitmapSource.Create</c>
    /// 会抛 ArgumentException，而不是显示错色。
    ///
    /// **标注必须在调用本方法之前画在原尺寸上**：DrawRect/DrawCircle 的坐标是绝对像素，
    /// 先缩再画会让框跑到零件外面去。正确顺序是「原图 → 画标注 → 两张各自缩 → 两张各自转」。
    /// </remarks>
    public static BitmapSource? ToBitmapSource(Mat? mat, int maxWidth = 0)
    {
        if (mat is null || mat.Empty()) return null;

        // 统一成 BGR 三通道。灰度图和 BGRA 都要转，否则 stride 与格式不匹配。
        Mat? converted = null;
        Mat? scaled = null;
        try
        {
            var source = mat;
            if (mat.Channels() == 1)
            {
                converted = new Mat();
                Cv2.CvtColor(mat, converted, ColorConversionCodes.GRAY2BGR);
                source = converted;
            }
            else if (mat.Channels() == 4)
            {
                converted = new Mat();
                Cv2.CvtColor(mat, converted, ColorConversionCodes.BGRA2BGR);
                source = converted;
            }

            // 降采样放在通道归一之后：CvtColor 的入参必须是原始通道数，
            // 先缩再转会让 GRAY2BGR 对着一张三通道图报错。
            if (maxWidth > 0 && source.Cols > maxWidth)
            {
                var scale = (double)maxWidth / source.Cols;
                var size = new OpenCvSharp.Size(maxWidth, Math.Max(1, (int)Math.Round(source.Rows * scale)));
                scaled = new Mat();
                Cv2.Resize(source, scaled, size, interpolation: InterpolationFlags.Area);
                source = scaled;
            }

            // 先把宽高取出来再拷数据：拷完要释放 Mat，
            // 释放后再读 Cols/Rows 就是访问已释放的非托管内存（AccessViolation，不是异常）。
            int width = source.Cols;
            int height = source.Rows;
            int stride = width * 3;
            var pixels = new byte[stride * height];
            Marshal.Copy(source.Data, pixels, 0, pixels.Length);

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            scaled?.Dispose();
            converted?.Dispose();
        }
    }
}
