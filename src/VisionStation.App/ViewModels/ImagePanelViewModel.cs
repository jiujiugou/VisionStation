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

    /// <summary>
    /// 图像区域顶部 Tab 页签（原图 / 检测）。
    ///
    /// 2026-10-23 删掉了「ROI」「边缘」两个页签：它们从来没有独立的图像源，
    /// 点开显示的还是原图 —— 摆了 4 个页签，实际只有 2 种内容。
    /// 这种"看着有功能、点了没变化"的入口比少一个功能更伤信任（同导航栏那 6 个死图标）。
    /// </summary>
    public ObservableCollection<ImageTab> Tabs { get; } = new()
    {
        new("原图", "Original"),
        new("检测", "Detection"),
    };

    /// <summary>当前选中的 Tab</summary>
    [ObservableProperty]
    private ImageTab _selectedTab;

    /// <summary>
    /// 根据选中的 Tab 返回对应的图像
    /// 检测 Tab → 标注图 / 其他 Tab → 原图
    /// </summary>
    public BitmapSource? CurrentImage => SelectedTab?.Key == "Detection" ? AnnotatedImage : OriginalImage;

    /// <summary>
    /// 没有图像时显示的说明。按引擎区分：
    /// 推驱动的引擎（VM）本来就不采图，黑屏是正常状态，必须写出来，
    /// 否则操作员只会认为程序坏了。
    /// </summary>
    public string EmptyHint { get; private set; } =
        "等待 VisionMaster 推送结果\nVM 模式下图像与标注在平台侧，本程序只接收结果";

    public ImagePanelViewModel()
        : this(null)
    {
    }

    public ImagePanelViewModel(VisionStation.Core.Engines.IInspectionEngine? engine)
    {
        if (engine is not null && engine is not VisionStation.Core.Engines.IPushDrivenEngine)
        {
            EmptyHint = "尚未收到检测结果";
        }

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
