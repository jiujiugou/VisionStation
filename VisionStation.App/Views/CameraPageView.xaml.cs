using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

public partial class CameraPageView
{
    public CameraPageView(CameraPageViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
