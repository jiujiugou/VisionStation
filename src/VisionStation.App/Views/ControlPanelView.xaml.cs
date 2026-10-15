using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

public partial class ControlPanelView
{
    public ControlPanelView(ControlPanelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
