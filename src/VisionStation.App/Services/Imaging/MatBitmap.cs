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
    /// <remarks>
    /// 单通道（黑白工业相机直出 8UC1）和四通道都会先转成三通道：
    /// WPF 的 <c>Bgr24</c> 要求 3 字节/像素，通道数不匹配时 <c>BitmapSource.Create</c>
    /// 会抛 ArgumentException，而不是显示错色。
    /// </remarks>
    public static BitmapSource? ToBitmapSource(Mat? mat)
    {
        if (mat is null || mat.Empty()) return null;

        // 统一成 BGR 三通道。灰度图和 BGRA 都要转，否则 stride 与格式不匹配。
        Mat? converted = null;
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
            converted?.Dispose();
        }
    }
}
