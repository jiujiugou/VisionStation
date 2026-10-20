using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Tests;

/// <summary>
/// 检测页的编辑态契约（S3a 从 MainWindow code-behind 搬出来后的唯一逻辑）。
///
/// 这里钉住三件事，都是改造前散在 MainWindow.ToggleEditMode 里、没有测试保护的：
///   1. 进入编辑要把当前图交给 ROI 编辑器（否则编辑器是空白画布，画框对不上底图）；
///   2. 原图与覆盖层互斥（同时可见会看到两层叠在一起的插图）；
///   3. 离开页面必须退出编辑（否则覆盖层会漂到相机页上）。
/// </summary>
public class InspectionPageViewModelTests
{
    [Fact]
    public void ToggleRoiEditor_EntersEditMode_HandsCurrentImageToEditor()
    {
        var page = TestPages.Inspection();
        var image = CreateBitmap();
        page.ImagePanel.OriginalImage = image;

        page.ToggleRoiEditorCommand.Execute(null);

        Assert.True(page.IsRoiEditorOpen);
        Assert.False(page.IsImagePanelVisible);
        Assert.Same(image, page.RoiEditor.Image);
    }

    [Fact]
    public void ToggleRoiEditor_SecondCall_LeavesEditMode_AndKeepsEditorImage()
    {
        var page = TestPages.Inspection();
        var image = CreateBitmap();
        page.ImagePanel.OriginalImage = image;

        page.ToggleRoiEditorCommand.Execute(null);
        page.ToggleRoiEditorCommand.Execute(null);

        Assert.False(page.IsRoiEditorOpen);
        Assert.True(page.IsImagePanelVisible);

        // 退出编辑不清编辑器里的图：再次进入时若面板还没新帧，用户看到的仍是上一张
        Assert.Same(image, page.RoiEditor.Image);
    }

    [Fact]
    public void ToggleRoiEditor_WithoutFrame_DoesNotThrow()
    {
        // 启动后还没采过图就按 F2：OriginalImage 为 null，不能 NRE
        var page = TestPages.Inspection();

        page.ToggleRoiEditorCommand.Execute(null);

        Assert.True(page.IsRoiEditorOpen);
        Assert.Null(page.RoiEditor.Image);
    }

    [Fact]
    public void ExitEditMode_ClosesOverlay_AndIsIdempotent()
    {
        var page = TestPages.Inspection();
        page.ToggleRoiEditorCommand.Execute(null);

        page.ExitEditMode();
        Assert.False(page.IsRoiEditorOpen);

        // 已经是关闭状态时再调一次（切页时会无条件调）不能抛，也不能翻回打开
        page.ExitEditMode();
        Assert.False(page.IsRoiEditorOpen);
    }

    private static BitmapSource CreateBitmap()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24, null, new byte[3], 3);
        bitmap.Freeze();
        return bitmap;
    }
}
