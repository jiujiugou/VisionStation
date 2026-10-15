using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

public partial class RoiEditorView : UserControl
{
    private readonly RoiEditorViewModel _vm;

    public RoiEditorView(RoiEditorViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;
    }

    /// <summary>Grid 容器尺寸变化 → 传入 Grid 实际宽高（带 Uniform 偏移计算在里面）</summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _vm.UpdateLayout(
            RootGrid.ActualWidth, RootGrid.ActualHeight,
            EditorImage.ActualWidth, EditorImage.ActualHeight);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _vm.MouseDown(e.GetPosition(Overlay));
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        _vm.MouseMove(e.GetPosition(Overlay));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _vm.MouseUp();
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedRoi != null)
            _vm.DeleteSelectedCommand.Execute(null);
    }
}
