using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Tests;

/// <summary>
/// ImagePanelViewModel 的绑定契约测试。
///
/// 为什么不启动 WPF：这里验证的是"当前该显示哪张图"的 VM 状态，
/// 不是 Image 控件怎么渲染。测试线程不需要 Application / Dispatcher。
/// </summary>
public class ImagePanelViewModelTests
{
    /// <summary>
    /// 启动后、第一帧检测完成前，面板必须显示原图。
    /// 如果 CurrentImage 的默认分支被改成 AnnotatedImage，启动阶段会是一片空白，
    /// 操作员无法确认相机是否真的出图。
    /// </summary>
    [Fact]
    public void DefaultTab_ShowsOriginalImage()
    {
        var vm = new ImagePanelViewModel();
        var original = CreateBitmap();
        var annotated = CreateBitmap();

        vm.OriginalImage = original;
        vm.AnnotatedImage = annotated;

        Assert.Same(original, vm.CurrentImage);
    }

    /// <summary>
    /// 切到"检测"页签后必须显示标注图。
    /// 这条守住的是 Tab → CurrentImage 的映射；映射错了会把原图当检测结果展示，
    /// 界面看起来正常，实际没有标注。
    /// </summary>
    [Fact]
    public void DetectionTab_ShowsAnnotatedImage()
    {
        var vm = new ImagePanelViewModel();
        var original = CreateBitmap();
        var annotated = CreateBitmap();

        vm.OriginalImage = original;
        vm.AnnotatedImage = annotated;
        vm.SelectedTab = vm.Tabs.Single(t => t.Key == "Detection");

        Assert.Same(annotated, vm.CurrentImage);
    }

    /// <summary>
    /// 1x1 Bgr24 只用来区分两个对象引用，不参与渲染。
    /// 必须 Freeze：生产代码里位图在后台线程创建，未冻结的 BitmapSource 交给 UI 会抛
    /// InvalidOperationException；测试替身也按同一契约走。
    /// </summary>
    private static BitmapSource CreateBitmap()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24, null, new byte[3], 3);
        bitmap.Freeze();
        return bitmap;
    }
}
