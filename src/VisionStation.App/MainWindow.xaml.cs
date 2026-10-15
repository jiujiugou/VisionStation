using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.ViewModels;
using VisionStation.App.Views;

namespace VisionStation.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _mainVm;
    private readonly ImagePanelViewModel _imagePanelVm;
    private readonly RoiEditorViewModel _roiEditorVm;

    public MainWindow(
        MainViewModel mainVm,
        SidebarView sidebar,
        ImagePanelView imagePanel,
        ResultPanelView resultPanel,
        LogPanelView logPanel,
        ControlPanelView controlPanel,
        StatusBarView statusBar,
        RoiEditorView roiEditor,
        CameraPageView cameraPage,
        ImagePanelViewModel imagePanelVm,
        RoiEditorViewModel roiEditorVm,
        SidebarViewModel sidebarVm)
    {
        InitializeComponent();

        _mainVm = mainVm;
        _imagePanelVm = imagePanelVm;
        _roiEditorVm = roiEditorVm;
        DataContext = mainVm;

        Sidebar.Content = sidebar;
        ImagePanel.Content = imagePanel;
        ResultPanel.Content = resultPanel;
        LogPanel.Content = logPanel;
        ControlPanel.Content = controlPanel;
        StatusBar.Content = statusBar;
        RoiEditor.Content = roiEditor;
        CameraPage.Content = cameraPage;

        // 导航 —— 侧边栏点击切换页面
        WeakReferenceMessenger.Default.Register<MainWindow, NavigateMessage>(this,
            (_, msg) =>
            {
                bool isDetection = msg.Value == "检测";
                InspectionPage.Visibility = isDetection ? Visibility.Visible : Visibility.Collapsed;
                CameraPage.Visibility = msg.Value == "相机" ? Visibility.Visible : Visibility.Collapsed;
                RoiEditor.Visibility = Visibility.Collapsed; // 切页退出编辑
                _isEditMode = false;
            });

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F2 && InspectionPage.Visibility == Visibility.Visible)
                ToggleEditMode();
        };
    }

    private bool _isEditMode;
    private void ToggleEditMode()
    {
        _isEditMode = !_isEditMode;

        if (_isEditMode)
        {
            _roiEditorVm.Image = _imagePanelVm.OriginalImage;
            ImagePanel.Visibility = Visibility.Collapsed;
            RoiEditor.Visibility = Visibility.Visible;
            Title = "VisionStation — [编辑ROI] 拖拽画框 · 移动 · 右下角缩放 · Delete/右键删除 · F2退出";
        }
        else
        {
            ImagePanel.Visibility = Visibility.Visible;
            RoiEditor.Visibility = Visibility.Collapsed;
            Title = "VisionStation - 金属零件视觉检测工作站";
        }
    }
}
