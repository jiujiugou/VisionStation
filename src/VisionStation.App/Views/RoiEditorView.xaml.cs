using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

public partial class RoiEditorView : UserControl
{
    /// <summary>VM 从 DataContext 取（S3b），与 ImagePanelView 同理。</summary>
    private RoiEditorViewModel? Vm => DataContext as RoiEditorViewModel;

    public RoiEditorView() => InitializeComponent();

    /// <summary>Grid 容器尺寸变化 → 传入 Grid 实际宽高（带 Uniform 偏移计算在里面）</summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.UpdateLayout(
            RootGrid.ActualWidth, RootGrid.ActualHeight,
            EditorImage.ActualWidth, EditorImage.ActualHeight);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Vm?.MouseDown(e.GetPosition(Overlay));
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        Vm?.MouseMove(e.GetPosition(Overlay));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        Vm?.MouseUp();
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm || vm.SelectedRoi == null) return;
        vm.DeleteSelectedCommand.Execute(null);
    }
}
