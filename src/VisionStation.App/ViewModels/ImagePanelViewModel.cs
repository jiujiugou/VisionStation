using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 图像面板 ViewModel
///
/// 职责：
///   1. 持有原始图像和标注图像两份 BitmapSource
///   2. 根据当前选中的 Tab（原图/检测/ROI/边缘）切换显示
///   3. MainViewModel 直接设置 OriginalImage / AnnotatedImage
///
/// 注意：这个 VM 不订阅消息，图像由 MainViewModel 直接赋值（性能考虑）
/// </summary>
public partial class ImagePanelViewModel : ObservableObject
{
    /// <summary>原始采集图像</summary>
    [ObservableProperty]
    private BitmapSource? _originalImage;

    /// <summary>叠加了 ROI 框、圆、缺陷轮廓、判定水印的图像</summary>
    [ObservableProperty]
    private BitmapSource? _annotatedImage;

    /// <summary>图像区域顶部 Tab 页签（原图 / 检测 / ROI / 边缘）</summary>
    public ObservableCollection<ImageTab> Tabs { get; } = new()
    {
        new("原图", "Original"),
        new("检测", "Detection"),
        new("ROI", "Roi"),
        new("边缘", "Edge"),
    };

    /// <summary>当前选中的 Tab</summary>
    [ObservableProperty]
    private ImageTab _selectedTab;

    /// <summary>
    /// 根据选中的 Tab 返回对应的图像
    /// 检测 Tab → 标注图 / 其他 Tab → 原图
    /// </summary>
    public BitmapSource? CurrentImage => SelectedTab?.Key == "Detection" ? AnnotatedImage : OriginalImage;

    public ImagePanelViewModel()
    {
        _selectedTab = Tabs[0]; // 默认显示原图
    }

    // ── 属性变化通知 —— 确保 Tab 切换或图像更新时 UI 刷新 ──
    partial void OnSelectedTabChanged(ImageTab value) => OnPropertyChanged(nameof(CurrentImage));
    partial void OnOriginalImageChanged(BitmapSource? value) => OnPropertyChanged(nameof(CurrentImage));
    partial void OnAnnotatedImageChanged(BitmapSource? value) => OnPropertyChanged(nameof(CurrentImage));
}

/// <summary>
/// 图像 Tab 标签数据
/// </summary>
/// <param name="Label">显示文字</param>
/// <param name="Key">内部标识</param>
public record ImageTab(string Label, string Key);
