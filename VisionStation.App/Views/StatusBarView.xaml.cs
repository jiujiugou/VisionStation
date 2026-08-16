using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

public partial class StatusBarView
{
    public StatusBarView(StatusBarViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
