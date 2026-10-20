using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.Services.Inspection;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 图像面板 ViewModel —— 持有原始图/标注图两份已冻结的位图，按页签决定显示哪一张。
///
/// 数据来源：订阅 <see cref="InspectionResultMessage"/>。
/// 改造前是检测编排方直接写本类的两个属性；S4 之后检测跑在后台线程上，
/// 直接写属性就等于"在后台线程改绑定了 UI 的状态"，所以改成消息驱动 ——
/// 消息由页面 VM 在 UI 线程上发出（见 <see cref="InspectionPageViewModel"/>）。
/// </summary>
public partial class ImagePanelViewModel : ObservableObject
{
    /// <summary>原始采集图像（已 Freeze，可跨线程）</summary>
    [ObservableProperty]
    private BitmapSource? _originalImage;

    /// <summary>叠加了 ROI 框、圆、缺陷轮廓、判定水印的图像（已 Freeze）</summary>
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

        WeakReferenceMessenger.Default.Register<ImagePanelViewModel, InspectionResultMessage>(this,
            static (vm, msg) => vm.Apply(msg.Value));
    }

    /// <summary>
    /// 应用一帧的图。调用方保证在 UI 线程上（消息由页面 VM 在 UI 线程广播）。
    /// </summary>
    private void Apply(InspectionRunResult result)
    {
        OriginalImage = result.OriginalImage;
        AnnotatedImage = result.AnnotatedImage;

        // 每帧自动切到"检测"页签：操作员看的是标注结果，不是原图。
        // 保留原行为（S4 只改线程归属，不改交互）。
        SelectedTab = Tabs[1];
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
