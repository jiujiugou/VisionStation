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
    public void 超宽图_缩到maxWidth并保持长宽比()
    {
        // 2448×2048 的一帧转成位图是 15MB，而上屏区不到 900px 宽。
        // 缩到 1280 是"每帧 7.5MB"这个内存估算唯一的落点：缩不掉，5 件/秒就是 150MB/s
        // 的纯拷贝，全砸在大对象堆上。尺寸错一个像素就说明 Resize 没走到或比例算歪了。
        using var mat = new Mat(1000, 2000, MatType.CV_8UC3, Scalar.All(64));

        var bitmap = MatBitmap.ToBitmapSource(mat, maxWidth: 1280);

        Assert.NotNull(bitmap);
        Assert.True(bitmap!.IsFrozen, "缩放分支产出的位图同样必须冻结，漏掉 Freeze 只在 UI 线程真绑定时才炸");
        Assert.Equal(1280, bitmap.PixelWidth);
        Assert.Equal(640, bitmap.PixelHeight);   // 1000 * 1280/2000，比例不能被拉变形
    }

    [Fact]
    public void 窄图_不超过maxWidth时不放大()
    {
        // 只缩不放：小图放大既更糊，分配的内存也比原图更多，与降采样的目的正好相反。
        // 这里若返回 1280×960，说明判断写成了 maxWidth != Cols 之类的"总是 Resize"。
        using var mat = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(64));

        var bitmap = MatBitmap.ToBitmapSource(mat, maxWidth: 1280);

        Assert.NotNull(bitmap);
        Assert.Equal(640, bitmap!.PixelWidth);
        Assert.Equal(480, bitmap.PixelHeight);
    }

    [Fact]
    public void maxWidth为0_不缩放()
    {
        // 0 是默认值，也是"这张图不上屏"（存图、离线回放）的调用方式。
        // 此时必须逐像素等于原图，不能被当成"缩到 0 宽"或被除零兜底成 1×1。
        using var mat = new Mat(1000, 2000, MatType.CV_8UC3, Scalar.All(64));

        var bitmap = MatBitmap.ToBitmapSource(mat, maxWidth: 0);

        Assert.NotNull(bitmap);
        Assert.Equal(2000, bitmap!.PixelWidth);
        Assert.Equal(1000, bitmap.PixelHeight);
    }

    [Fact]
    public void 灰度图走降采样_尺寸格式冻结三者都要对()
    {
        // 工业相机直出的是 8UC1。这条把「灰度 → 三通道 → 缩放 → 上屏」整条链钉死：
        // 输出必须是缩到 1280×640 的 Bgr24。少掉通道归一，stride 与 Bgr24 对不上会在
        // Create 里抛 ArgumentException；少掉缩放，则灰度图仍然按原尺寸占着内存。
        using var mat = new Mat(1000, 2000, MatType.CV_8UC1, Scalar.All(128));

        var bitmap = MatBitmap.ToBitmapSource(mat, maxWidth: 1280);

        Assert.NotNull(bitmap);
        Assert.True(bitmap!.IsFrozen, "灰度降采样路径产出的位图也必须冻结，否则跨线程绑定会抛");
        Assert.Equal(PixelFormats.Bgr24, bitmap.Format);
        Assert.Equal(1280, bitmap.PixelWidth);
        Assert.Equal(640, bitmap.PixelHeight);
    }

    [Fact]
    public void 宽2448的原图_降采样后高按等比取整_长宽比误差在容差内()
    {
        // 现场真正的尺寸就是 2448×2048，而它不是 1280 的整数倍。
        // 2000×1000 这种"正好除得尽"的样例测不出取整误差 —— 比例算歪了它照样通过。
        // 2048 * 1280/2448 = 1070.85 → 取整 1071；容差 0.5% 只够吸收这一像素级的取整。
        using var mat = new Mat(2048, 2448, MatType.CV_8UC3, Scalar.All(64));

        var bitmap = MatBitmap.ToBitmapSource(mat, maxWidth: 1280);

        Assert.NotNull(bitmap);
        Assert.Equal(1280, bitmap!.PixelWidth);
        Assert.Equal(1071, bitmap.PixelHeight);

        var expected = 2448.0 / 2048.0;
        var actual = (double)bitmap.PixelWidth / bitmap.PixelHeight;
        Assert.True(Math.Abs(actual - expected) / expected < 0.005,
            $"长宽比被拉变形：原 {expected:0.0000}，实际 {actual:0.0000}");
    }

    [Fact]
    public void maxWidth比原宽小1像素_仍然缩小()
    {
        // 边界：只差 1 像素也要缩。判断写成 Cols > maxWidth + 1 之类留容差的写法，
        // 会让"几乎同宽"的图原位不动，而调用方以为自己已经限宽了。
        using var mat = new Mat(800, 1000, MatType.CV_8UC3, Scalar.All(64));

        var bitmap = MatBitmap.ToBitmapSource(mat, maxWidth: 999);

        Assert.NotNull(bitmap);
        Assert.Equal(999, bitmap!.PixelWidth);
        Assert.Equal(799, bitmap.PixelHeight);   // 800 * 999/1000 = 799.2 → 799
    }

    [Fact]
    public void 四通道图走降采样_先归一通道再缩放()
    {
        // BGRA → BGR 必须在 Resize 之前：反过来先缩再转，CvtColor 面对的就是一张
        // 已经缩过的四通道图，转出来的 stride 与 Bgr24 对不上，Create 直接抛 ArgumentException。
        using var mat = new Mat(1000, 2000, MatType.CV_8UC4, new Scalar(1, 2, 3, 255));

        var bitmap = MatBitmap.ToBitmapSource(mat, maxWidth: 1280);

        Assert.NotNull(bitmap);
        Assert.Equal(PixelFormats.Bgr24, bitmap!.Format);
        Assert.Equal(1280, bitmap.PixelWidth);
        Assert.Equal(640, bitmap.PixelHeight);

        // 只拷左上角 1×1 —— 不指定区域的重载会按整张 1280×640 拷贝，
        // 而 3 字节的缓冲区放不下，CopyPixels 会直接抛 stride 参数越界。
        var pixel = new byte[3];
        bitmap.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 3, 0);
        Assert.Equal(new byte[] { 1, 2, 3 }, pixel);   // BGR 顺序不能被归一化打乱
    }

    [Fact]
    public void maxWidth为1_极宽图不崩溃且高不塌成0()
    {
        // 2000 宽缩到 1 宽时，算出来的高取整后是 0，而 BitmapSource.Create 对 0 高只抛异常。
        // 实现用 Math.Max(1, ...) 兜住了；这条钉住那个兜底，别在"简化"时被抹掉。
        using var mat = new Mat(1000, 2000, MatType.CV_8UC3, Scalar.All(64));

        var bitmap = MatBitmap.ToBitmapSource(mat, maxWidth: 1);

        Assert.NotNull(bitmap);
        Assert.Equal(1, bitmap!.PixelWidth);
        Assert.True(bitmap.PixelHeight >= 1, $"高不能塌成 0，实际 {bitmap.PixelHeight}");
    }

    [Fact]
    public void 负maxWidth按不缩放处理_不抛异常()
    {
        // 文档只定义了 0 = 不缩放，负数没有定义。实现用 maxWidth > 0 判断，负数自然落进不缩放。
        // 这条把"没有定义"钉在安全的一侧：以后若改成 maxWidth != 0，这里会立刻红。
        using var mat = new Mat(1000, 2000, MatType.CV_8UC3, Scalar.All(64));

        var bitmap = MatBitmap.ToBitmapSource(mat, maxWidth: -1);

        Assert.NotNull(bitmap);
        Assert.Equal(2000, bitmap!.PixelWidth);
        Assert.Equal(1000, bitmap.PixelHeight);
    }

    [Fact]
    public void 降采样后像素值仍是原值_没有拷到未初始化内存()
    {
        // 尺寸对不代表内容是这张图：Resize 走了、但拷贝时读错指针（例如读的是已释放的 Mat，
        // 或读的是全零的临时缓冲），尺寸断言全过、画面全黑 —— 那种 bug 只能靠像素值抓。
        // 纯色图缩完每一像素仍应是原色（Area 是区域平均，均匀区域平均后不变）。
        using var mat = new Mat(1000, 2000, MatType.CV_8UC3, Scalar.All(64));

        var bitmap = MatBitmap.ToBitmapSource(mat, maxWidth: 1280)!;

        var stride = bitmap.PixelWidth * 3;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        byte min = byte.MaxValue;
        byte max = byte.MinValue;
        foreach (var b in pixels)
        {
            if (b < min) min = b;
            if (b > max) max = b;
        }

        // 容差 1：Area 平均在边界上可能有一档舍入差
        Assert.InRange(min, (byte)63, (byte)65);
        Assert.InRange(max, (byte)63, (byte)65);
    }

    [Fact]
    public void 降采样分支同样不吞掉入参()
    {
        // 缩放会新建一个 Mat 在 finally 里释放 —— 释放的必须是那个临时的，
        // 不能顺手把入参也放了：调用方 finally 里还要再 Dispose 一次。
        using var mat = new Mat(1000, 2000, MatType.CV_8UC3, Scalar.All(64));

        _ = MatBitmap.ToBitmapSource(mat, maxWidth: 1280);

        Assert.False(mat.Empty());
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
