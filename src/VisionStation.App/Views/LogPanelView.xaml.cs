using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

public partial class LogPanelView
{
    public LogPanelView(LogPanelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
