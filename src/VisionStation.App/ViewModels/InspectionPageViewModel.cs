using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 检测页 ViewModel —— 只回答两件事：这一页由哪些面板组成、编辑态是否打开。
///
/// 为什么需要它：改造前这五块面板是 <c>MainWindow</c> 的构造函数参数，
/// 由窗口用 <c>ContentControl.Content = view</c> 一个个塞进版面。
/// 结果是"页面"这个概念根本不存在——窗口既当外壳又当页面，
/// 换页面只能靠改 <c>Visibility</c>，F2 的编辑态也只能由窗口的 code-behind 管。
///
/// 现在的归属：
///   - 页面归这个 VM（子面板是组合关系，父页面持有子 VM，不算跨页通信）；
///   - 编辑态归这个 VM（<see cref="IsRoiEditorOpen"/>），视图只做绑定；
///   - 检测链路仍归 <see cref="MainViewModel"/>（S4 再抽 workflow）。
/// </summary>
public partial class InspectionPageViewModel : ObservableObject
{
    public ImagePanelViewModel ImagePanel { get; }
    public ResultPanelViewModel ResultPanel { get; }
    public LogPanelViewModel LogPanel { get; }
    public ControlPanelViewModel ControlPanel { get; }
    public RoiEditorViewModel RoiEditor { get; }

    /// <summary>ROI 编辑覆盖层是否打开。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImagePanelVisible))]
    private bool _isRoiEditorOpen;

    /// <summary>
    /// 原图面板与编辑覆盖层互斥：编辑时原图让位，退出编辑后回来。
    /// 用派生属性而不是在 XAML 里加反向转换器 —— 少一个转换器，也便于单测。
    /// </summary>
    public bool IsImagePanelVisible => !IsRoiEditorOpen;

    public InspectionPageViewModel(
        ImagePanelViewModel imagePanel,
        ResultPanelViewModel resultPanel,
        LogPanelViewModel logPanel,
        ControlPanelViewModel controlPanel,
        RoiEditorViewModel roiEditor)
    {
        ImagePanel = imagePanel;
        ResultPanel = resultPanel;
        LogPanel = logPanel;
        ControlPanel = controlPanel;
        RoiEditor = roiEditor;
    }

    /// <summary>F2：进入/退出 ROI 编辑。</summary>
    [RelayCommand]
    private void ToggleRoiEditor()
    {
        if (!IsRoiEditorOpen)
        {
            // 进入编辑时把当前图交给编辑器：编辑器要看着底图画框。
            // 还没采过图时 OriginalImage 为 null，编辑器自己显示空态。
            RoiEditor.Image = ImagePanel.OriginalImage;
        }

        IsRoiEditorOpen = !IsRoiEditorOpen;
    }

    /// <summary>
    /// 离开本页时调用。编辑态不退出的话，覆盖层会跟着用户漂到别的页面上去。
    /// </summary>
    public void ExitEditMode() => IsRoiEditorOpen = false;
}
