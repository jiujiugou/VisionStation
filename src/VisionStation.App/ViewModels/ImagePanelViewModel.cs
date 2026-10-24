using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.Services.Inspection;
using VisionStation.Core.Engines;
using VisionStation.Devices.Vm;

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
    /// <summary>
    /// 图像是否由外部引擎推来（VM）。决定等待态怎么说话：
    /// VM 模式下"还没图"是正常态，自采模式下"还没图"意味着没点开始。
    /// </summary>
    private readonly bool _isPushDriven;

    /// <summary>最近一次真正拿到图像的时间；null = 从启动到现在一张都没成功过</summary>
    private DateTime? _lastImageTime;

    /// <summary>原始采集图像（已 Freeze，可跨线程）</summary>
    [ObservableProperty]
    private BitmapSource? _originalImage;

    /// <summary>叠加了 ROI 框、圆、缺陷轮廓、判定水印的图像（已 Freeze）</summary>
    [ObservableProperty]
    private BitmapSource? _annotatedImage;

    /// <summary>空态主文案：一句话说清现在是什么状态</summary>
    [ObservableProperty]
    private string _emptyTitle = "";

    /// <summary>
    /// 空态说明：为什么没有图。
    ///
    /// 这四种情况在屏幕上都只是一块黑，但处理方式完全不同：
    /// 还没收到结果（等）、VM 没给路径（去 VM 配）、路径不存在（查盘符）、
    /// 读图失败（查权限）。不说原因，技术员只能猜，而且最容易猜成
    /// "这软件本来就不显示图"—— 然后谁也不去修。
    /// </summary>
    [ObservableProperty]
    private string _emptyDetail = "";

    /// <summary>空态指引：现在该去做什么。没有可做的事时为空（不占位）</summary>
    [ObservableProperty]
    private string _emptyAction = "";

    /// <summary>是否有指引行（空字符串时那一行不占位）</summary>
    [ObservableProperty]
    private bool _hasEmptyAction;

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

    public ImagePanelViewModel()
        : this(null)
    {
    }

    public ImagePanelViewModel(IInspectionEngine? engine)
    {
        _isPushDriven = engine is IPushDrivenEngine;

        _selectedTab = Tabs[0]; // 默认显示原图
        ShowWaitingState();

        WeakReferenceMessenger.Default.Register<ImagePanelViewModel, InspectionResultMessage>(this,
            static (vm, msg) => vm.Apply(msg.Value));

        WeakReferenceMessenger.Default.Register<ImagePanelViewModel, VmLinkStatusMessage>(this,
            static (vm, msg) => vm.ApplyLinkStatus(msg.Value));
    }

    /// <summary>
    /// 还没收到任何结果时的空态。两种引擎说不同的话：
    /// VM 模式是"等它推"，自采模式是"你还没让它跑"。
    ///
    /// 措辞上刻意不提 "VisionMaster"：产线操作员不认识这个词，
    /// 他只认"等结果""没连上""断了"这三种状态。
    /// </summary>
    private void ShowWaitingState()
    {
        if (_isPushDriven)
        {
            EmptyTitle = "等待检测结果";
            EmptyDetail = "尚未收到任何结果";
        }
        else
        {
            EmptyTitle = "尚未开始检测";
            EmptyDetail = "点「开始」或等待触发";
        }

        EmptyAction = "";
        HasEmptyAction = false;
    }

    /// <summary>
    /// 链路状态变化 —— **只在没有图的时候改空态**。
    ///
    /// 有图时不覆盖：那张图是最新一件的判定，比"链路断了"更该占着屏幕；
    /// 断流本身已经有日志告警兜着，把图换成一句警告等于把证据也擦掉了。
    ///
    /// 调用方保证在 UI 线程上（VmInspectionDriver 已切回 UI 线程再发消息）。
    /// </summary>
    private void ApplyLinkStatus(VmLinkStatus status)
    {
        // 自采模式没有"平台连没连"这回事，链路状态与它无关
        if (!_isPushDriven) return;

        if (OriginalImage is not null) return;

        switch (status.State)
        {
            case VmLinkState.NoClient:
                EmptyTitle = "平台未连接";
                EmptyDetail = $"已监听 {status.Endpoint}，等待平台连上来";
                EmptyAction = "检查检测平台是否已启动，通信管理里的 IP 与端口是否和上面一致";
                HasEmptyAction = true;
                break;

            case VmLinkState.Stalled:
                var since = status.SinceLast is { } s
                    ? $"已 {s.TotalSeconds:0.#} 秒"
                    : "长时间";
                EmptyTitle = "已断流";
                EmptyDetail = $"{since}没有收到检测结果（阈值 {status.StallTimeout.TotalSeconds:0} 秒）";
                EmptyAction = "检查平台流程是否在连续运行、触发信号是否到位、推数据模块是否被执行";
                HasEmptyAction = true;
                break;

            case VmLinkState.Healthy:
                // 恢复后回到"等第一件"的正常说法，别把人留在断流的画面里
                if (_lastImageTime is null)
                {
                    EmptyTitle = "等待第一件检测";
                    EmptyDetail = "平台已连接，尚未收到结果";
                    EmptyAction = "";
                    HasEmptyAction = false;
                }
                break;
        }
    }

    /// <summary>
    /// 应用一帧的图。调用方保证在 UI 线程上（消息由页面 VM 在 UI 线程广播）。
    /// </summary>
    private void Apply(InspectionRunResult result)
    {
        if (result.OriginalImage is not null)
        {
            OriginalImage = result.OriginalImage;
            AnnotatedImage = result.AnnotatedImage;
            _lastImageTime = result.Report.Timestamp;

            EmptyTitle = "";
            EmptyDetail = "";
            EmptyAction = "";
            HasEmptyAction = false;

            // 每帧自动切到"检测"页签：操作员看的是标注结果，不是原图。
            // 保留原行为（S4 只改线程归属，不改交互）。
            SelectedTab = Tabs[1];
            return;
        }

        // 无图：必须清空。
        //
        // 这里绝不"保留上一帧"—— 留着就是把上一件的图当成本件显示，
        // 照错的图判断比看黑屏危险得多。黑屏只是难看，错图会误导。
        // 清空之后，把"为什么没有"写在屏幕中间，这是黑屏唯一该有的内容。
        OriginalImage = null;
        AnnotatedImage = null;

        EmptyTitle = "本帧没有图像";
        EmptyDetail = result.ImageUnavailableReason ?? "未收到图像，也未给出原因";

        if (_lastImageTime is { } last)
        {
            // 有历史图：告诉他上一条能看到的是哪一件，便于分清"刚开始坏"还是"从来没通"
            EmptyAction = $"上一张有图像的是 {last.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";
            HasEmptyAction = true;
        }
        else if (_isPushDriven)
        {
            // 一张都没成功过：这是链路没接通，不是偶发失败，直接给配置动作
            EmptyAction = "VM 侧在流程末尾加「存图」模块并开启渲染图输出，"
                        + "再把文件路径放进发送数据的 imagePath 字段";
            HasEmptyAction = true;
        }
        else
        {
            EmptyAction = "";
            HasEmptyAction = false;
        }
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
