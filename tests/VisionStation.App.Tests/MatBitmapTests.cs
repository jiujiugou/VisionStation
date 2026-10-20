using System.Windows.Media;
using OpenCvSharp;
using VisionStation.App.Services.Imaging;

namespace VisionStation.App.Tests;

/// <summary>
/// 位图转换的契约。
///
/// 这里只有一条是真正重要的：**返回必须已冻结**。
/// 检测跑在后台线程上（S4），冻结不到位会在 UI 绑定那一刻抛
/// "该对象归另一个线程所有" —— 而且只在真实运行时出现，编译和单测都看不见。
/// </summary>
public class MatBitmapTests
{
    [Fact]
    public void 灰度图_转出来是已冻结的Bgr24_尺寸不变()
    {
        using var mat = new Mat(4, 6, MatType.CV_8UC1, Scalar.All(128));

        var bitmap = MatBitmap.ToBitmapSource(mat);

        Assert.NotNull(bitmap);
        Assert.True(bitmap!.IsFrozen, "后台线程创建的位图必须冻结，否则不能跨线程绑定");
        Assert.Equal(PixelFormats.Bgr24, bitmap.Format);
        Assert.Equal(6, bitmap.PixelWidth);
        Assert.Equal(4, bitmap.PixelHeight);
    }

    [Fact]
    public void 三通道图_像素值原样搬运()
    {
        using var mat = new Mat(1, 1, MatType.CV_8UC3, new Scalar(10, 20, 30));   // BGR

        var bitmap = MatBitmap.ToBitmapSource(mat)!;

        var pixel = new byte[3];
        bitmap.CopyPixels(pixel, 3, 0);

        Assert.Equal(new byte[] { 10, 20, 30 }, pixel);
    }

    [Fact]
    public void 四通道图_转成三通道不抛()
    {
        using var mat = new Mat(2, 2, MatType.CV_8UC4, new Scalar(1, 2, 3, 255));

        var bitmap = MatBitmap.ToBitmapSource(mat);

        Assert.NotNull(bitmap);
        Assert.Equal(PixelFormats.Bgr24, bitmap!.Format);
    }

    [Fact]
    public void 空图返回null_调用方按没有图处理()
    {
        using var empty = new Mat();

        Assert.Null(MatBitmap.ToBitmapSource(empty));
        Assert.Null(MatBitmap.ToBitmapSource(null));
    }

    [Fact]
    public void 转换不吞掉入参_调用方仍然可以释放它()
    {
        // 内部会为彩转灰新建临时 Mat 并释放，入参的所有权必须留在调用方手里，
        // 否则工作流 finally 里的 Dispose 会变成二次释放。
        using var mat = new Mat(2, 2, MatType.CV_8UC1, Scalar.All(64));

        _ = MatBitmap.ToBitmapSource(mat);

        Assert.False(mat.Empty());
        mat.Dispose();
    }
}
