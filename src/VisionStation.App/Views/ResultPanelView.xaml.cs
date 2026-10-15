using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

public partial class ResultPanelView
{
    public ResultPanelView(ResultPanelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
