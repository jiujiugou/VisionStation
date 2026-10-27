using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

/// <summary>
/// 图像显示区的视图层：滚轮缩放、拖拽平移、F 键复位、像素坐标读数。
///
/// <para>
/// 这些都留在 code-behind 而不是 VM 里，因为它们全是**纯视觉交互**：
/// 缩放比例和偏移量只影响这一块画布，不进数据、不入库、不影响判定。
/// 放进 VM 的结果是每次拖一下鼠标都要发属性通知，而除了这一个 View 没人关心。
/// </para>
/// <para>
/// 与 VM 的交界只有一个：<c>DataContext as ImagePanelViewModel</c>，且只读（取图像尺寸）。
/// </para>
/// </summary>
public partial class ImagePanelView : UserControl
{
    // 缩放/平移状态。全部只在 UI 线程读写，不需要锁。
    private double _zoom = 1.0;
    private Point _panStart;
    private Point _mousePos;
    private bool _isPanning;

    /// <summary>
    /// VM 从 DataContext 取（S3b）：视图由 DataTemplate 创建，构造函数没有参数，
    /// 也不允许再自己 new 一个 VM —— 那样界面上显示的就不是检测链路写入的那一份。
    /// </summary>
    private ImagePanelViewModel? Vm => DataContext as ImagePanelViewModel;

    public ImagePanelView() => InitializeComponent();

    // ════════════════════════════════════════════════
    //  滚轮缩放（以鼠标位置为中心，zoom 1x~20x）
    // ════════════════════════════════════════════════
    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double oldZoom = _zoom;
        _zoom += e.Delta > 0 ? 0.2 : -0.2;
        _zoom = Math.Max(0.5, Math.Min(20.0, _zoom));

        // 以鼠标位置为中心缩放
        var mousePos = e.GetPosition(ImageHost);
        double ratio = _zoom / oldZoom;
        PanTransform.X = mousePos.X - ratio * (mousePos.X - PanTransform.X);
        PanTransform.Y = mousePos.Y - ratio * (mousePos.Y - PanTransform.Y);

        ApplyZoom();
        e.Handled = true;
    }

    // ════════════════════════════════════════════════
    //  拖拽平移（只在缩放后有效）
    // ════════════════════════════════════════════════
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_zoom <= 1.01) return; // 没缩放不劫持鼠标

        _isPanning = true;
        _panStart = e.GetPosition(ImageHost);
        ImageDisplay.Cursor = Cursors.ScrollAll;
        ImageHost.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // 实时记录鼠标位置并显示像素坐标
        _mousePos = e.GetPosition(ImageDisplay);
        var vm = Vm;
        double imgW = vm?.CurrentImage?.PixelWidth ?? 0;
        double imgH = vm?.CurrentImage?.PixelHeight ?? 0;
        double dispW = ImageDisplay.ActualWidth;
        double dispH = ImageDisplay.ActualHeight;

        // 守卫：窗口刚打开时 ActualWidth 还是 0，除零得到 Infinity，
        // (int)Infinity 在 .NET 上会截成 int.MinValue —— 界面上就出现
        // "(-2147483648, -2147483648) px" 这种鬼数字。
        if (dispW > 0 && dispH > 0 && imgW > 0 && imgH > 0)
        {
            var px = (int)(_mousePos.X / dispW * imgW);
            var py = (int)(_mousePos.Y / dispH * imgH);
            CoordLabel.Text = $"({px}, {py}) px | {_zoom:F1}x";
        }
        else
        {
            // 还没有图（或布局尚未完成）——显示占位符，不要显示越界数字
            CoordLabel.Text = $"(—, —) px | {_zoom:F1}x";
        }

        if (!_isPanning) return;

        var pos = e.GetPosition(ImageHost);
        PanTransform.X += pos.X - _panStart.X;
        PanTransform.Y += pos.Y - _panStart.Y;
        _panStart = pos;
    }

    /// <summary>
    /// 结束平移。**必须无条件释放鼠标捕获**（即使这次按下没真正开始平移）——
    /// 捕获不释放会让整个窗口之后都收不到鼠标事件，看起来像界面卡死。
    /// </summary>
    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false;
        ImageDisplay.Cursor = Cursors.Arrow;
        ImageHost.ReleaseMouseCapture();
    }

    // ════════════════════════════════════════════════
    //  F 键 → Fit Window（复位到 1x + 居中）
    // ════════════════════════════════════════════════
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F)
        {
            _zoom = 1.0;
            PanTransform.X = 0;
            PanTransform.Y = 0;
            ApplyZoom();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 把 <see cref="_zoom"/> 写进 ScaleTransform。
    /// 只改缩放、不动平移 —— 平移由滚轮换算和拖拽各自负责，这里再动一次会互相打架。
    /// </summary>
    private void ApplyZoom()
    {
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
    }
}
