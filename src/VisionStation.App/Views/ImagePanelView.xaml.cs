using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

public partial class ImagePanelView : UserControl
{
    private readonly ImagePanelViewModel _vm;

    // 缩放/平移状态
    private double _zoom = 1.0;
    private Point _panStart;
    private Point _mousePos;
    private bool _isPanning;

    public ImagePanelView(ImagePanelViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImagePanelViewModel.CurrentImage))
                ImageDisplay.Source = _vm.CurrentImage;
        };
    }

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
        double imgW = _vm.CurrentImage?.PixelWidth ?? 0;
        double imgH = _vm.CurrentImage?.PixelHeight ?? 0;
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

    private void ApplyZoom()
    {
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
    }
}
