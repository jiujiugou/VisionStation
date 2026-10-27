using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    /// 图像是否由外部引擎推来（VM）。决定等待态怎么说话。
    ///
    /// <para>
    /// <b>现状：恒为 true。</b>自采模式（程序自己截图后检测）随相机层在 2026-10-26 删除，
    /// 运行时唯一的引擎 <c>VmExternalEngine</c> 实现了 <see cref="IPushDrivenEngine"/>。
    /// 保留这个分支是因为下面的 <c>ShowWaitingState</c> / <c>ApplyLinkStatus</c> 依赖它
    /// 区分"等平台推"和"等你点开始"，而测试里仍会构造非推驱动的替身。
    /// </para>
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

    // ═══════════════════════════════════════════════════════════
    //  NG 回看（2026-10-24 加入）+ NG 最短停留（2026-10-26）
    //
    //  产线上操作员发现 NG 要叫人来看，但下一件一来图就换了 —— 等人走过来，
    //  刚才那个 NG 已经找不到了。历史页能查，可"喊人来看"发生在几秒钟内。
    //
    //  两层处理，各管一段：
    //    · 主图的**最短停留窗口**（NgHoldDuration）：NG 上屏后至少留这么久，
    //      期间到达的 OK 件不抢画面。这是"抬头能看见"的那一层。
    //    · 最近一张 NG 的**缩略回看**：始终指向最新 NG，不受停留窗口影响。
    //      这是"等人走过来"的那一层。
    //
    //  为什么不做成"NG 就永久钉住不换"：那会让操作员以为产线停了，
    //  而且下一件 NG 来的时候界面已经不反映现场了 —— 停在历史帧上的监视器比黑屏更危险。
    //  所以停留是**有期限**的，过期后主图恢复跟随最新件。
    //
    //  这里**不阻塞产线**：图继续进、库继续记、PLC 继续回写。
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// NG 画面在**主图**上的最短停留时长（默认 2 秒）。
    ///
    /// <para>
    /// 业务参数，不是算出来的：人从低头到抬头看屏幕约 1~2 秒，
    /// 剔除气缸动作 0.5~1.5 秒，取 2 秒是覆盖这两件事的下限。
    /// **现场节拍不同就要改这个数**，别当成技术常数。
    /// </para>
    /// <para>
    /// 做成实例字段（构造可覆盖）而不是常量，是为了让测试把窗口压到几十毫秒 ——
    /// 否则验证"窗口内 OK 不上屏"每个用例都要真等 2 秒，既慢又容易偶发失败。
    /// 默认值仍是 2 秒，生产行为一个字不变。
    /// </para>
    /// </summary>
    private readonly TimeSpan _ngHoldDuration;

    /// <summary>NG 保持的默认时长（现场节拍变了先改这里）。</summary>
    private static readonly TimeSpan DefaultNgHoldDuration = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 主图的保持截止时刻（UTC）。过期的判断只发生在"下一件到达时"，
    /// 不起定时器 —— 产线停了就该一直停在 NG 那一帧，这是对的。
    /// </summary>
    private DateTime _ngHoldUntilUtc = DateTime.MinValue;

    /// <summary>
    /// 主图当前是否停留在 NG 上（还在保持窗口内）。
    /// UI 用它显示"保持中"角标：不标出来，操作员会把停住的画面当成界面卡死。
    /// </summary>
    [ObservableProperty]
    private bool _isNgHeld;

    /// <summary>最近一张 NG 的原图（已 Freeze）</summary>
    private BitmapSource? _recentNgOriginal;

    /// <summary>最近一张 NG 的标注图（已 Freeze）</summary>
    private BitmapSource? _recentNgAnnotated;

    /// <summary>
    /// 右下角缩略图用的位图：优先标注图（一眼能看出坏在哪），没有就原图。
    /// 与主图不同步 —— 主图会随 OK 件前进，这张始终是最新 NG。
    /// </summary>
    public BitmapSource? RecentNgImage => _recentNgAnnotated ?? _recentNgOriginal;

    /// <summary>是否有可回看的 NG</summary>
    [ObservableProperty]
    private bool _hasRecentNg;

    /// <summary>回看入口上的文字：时间 + NG</summary>
    [ObservableProperty]
    private string _recentNgText = "";

    /// <summary>当前是否停在 NG 回看画面上（true 时图像区显示历史 NG，不是最新一帧）</summary>
    [ObservableProperty]
    private bool _isReviewingNg;

    /// <summary>回看按钮上的文字。放在 VM 里而不是 XAML 的 DataTrigger 里：一句话的事，少一层触发器。</summary>
    public string NgReviewButtonText => IsReviewingNg ? "返回实时" : "回看";

    /// <summary>回看 / 返回实时。没有 NG 可看时不做任何事（按钮本身不可见）。</summary>
    [RelayCommand]
    private void ToggleNgReview()
    {
        if (!HasRecentNg) return;
        IsReviewingNg = !IsReviewingNg;
    }

    /// <summary>
    /// 根据选中的 Tab 返回对应的图像。
    ///
    /// 回看态下取的是"最近一张 NG"，与实时态共用同一套 Tab：
    /// 看原图还是看标注，是同一个问题的两种看法，不该分成四个状态。
    /// </summary>
    public BitmapSource? CurrentImage
    {
        get
        {
            var useAnnotated = SelectedTab?.Key == "Detection";

            if (IsReviewingNg)
                return useAnnotated ? _recentNgAnnotated : _recentNgOriginal;

            return useAnnotated ? AnnotatedImage : OriginalImage;
        }
    }

    /// <summary>无引擎版本：按"非推驱动"初始化。只有测试和设计器会用到。</summary>
    public ImagePanelViewModel()
        : this(null, null)
    {
    }

    /// <summary>
    /// 构造即订阅两条消息并进入等待态。
    ///
    /// <para>
    /// 注意 <c>ShowWaitingState()</c> 在构造里发属性通知：此时 XAML 可能还没绑上来，
    /// 通知会落空 —— 但没关系，属性值本身已经设好，绑定建立时会读到。
    /// </para>
    /// </summary>
    /// <param name="engine">
    /// 用来判断"图是不是外面推来的"。没有引擎（单测）时按自采模式说话。
    /// </param>
    /// <param name="ngHoldDuration">
    /// NG 主图保持时长；null = 用默认 2 秒。**只有测试会传别的值**：
    /// 压到几十毫秒才能快速验证"窗口内 OK 不上屏"，真等 2 秒既慢又会偶发失败。
    /// </param>
    public ImagePanelViewModel(IInspectionEngine? engine, TimeSpan? ngHoldDuration = null)
    {
        _isPushDriven = engine is IPushDrivenEngine;
        _ngHoldDuration = ngHoldDuration ?? DefaultNgHoldDuration;

        _selectedTab = Tabs[0]; // 默认显示原图
        ShowWaitingState();

        WeakReferenceMessenger.Default.Register<ImagePanelViewModel, InspectionResultMessage>(this,
            static (vm, msg) => vm.Apply(msg.Value));

        WeakReferenceMessenger.Default.Register<ImagePanelViewModel, VmLinkStatusMessage>(this,
            static (vm, msg) => vm.ApplyLinkStatus(msg.Value));

        WeakReferenceMessenger.Default.Register<ImagePanelViewModel, ClearAllMessage>(this,
            static (vm, _) => vm.ClearDisplay());
    }

    /// <summary>
    /// 清掉画面，退回"等待结果"的空态。
    ///
    /// <para>
    /// 要连 NG 保持窗口一起清：不清的话，交接班按一下清空，2 秒内来一帧 OK 仍会被旧窗口挡住
    /// （那是"NG 要钉住"的逻辑），操作员看到的是"清了又没清"。
    /// </para>
    /// <para>
    /// 最近 NG 回看也要清：那是上一班的不良品证据，留给下一班只会让人以为是自己这班的。
    /// </para>
    /// </summary>
    private void ClearDisplay()
    {
        OriginalImage = null;
        AnnotatedImage = null;
        _lastImageTime = null;

        // 撤销 NG 保持窗口
        _ngHoldUntilUtc = DateTime.MinValue;
        IsNgHeld = false;

        // 最近 NG 回看
        _recentNgOriginal = null;
        _recentNgAnnotated = null;
        HasRecentNg = false;
        RecentNgText = "";
        IsReviewingNg = false;

        ShowWaitingState();
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
            var isNg = !result.Report.IsOk;
            var now = DateTime.UtcNow;

            _lastImageTime = result.Report.Timestamp;

            // NG 一律立刻上屏并重开保持窗口；OK 件只在窗口之外才替换主图。
            // 窗口内被跳过的 OK 件没有丢：它照样入库、照样回写 PLC，只是不上屏 ——
            // 那块屏幕此刻的职责是"让刚才那件 NG 留在人眼里"。
            var shouldShow = isNg || now >= _ngHoldUntilUtc;

            if (isNg)
            {
                // 缩略回看始终指向最新 NG，不受主图保持窗口影响
                _recentNgOriginal = result.OriginalImage;
                _recentNgAnnotated = result.AnnotatedImage ?? result.OriginalImage;
                HasRecentNg = true;
                RecentNgText = $"{result.Report.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} NG";
                OnPropertyChanged(nameof(RecentNgImage));
                _ngHoldUntilUtc = now + _ngHoldDuration;
            }

            if (!shouldShow) return;

            // 顺序很重要 —— 先切回实时再赋值：否则正在回看旧 NG 时会先闪一下旧图再跳到新图。
            IsReviewingNg = false;

            OriginalImage = result.OriginalImage;
            AnnotatedImage = result.AnnotatedImage;
            IsNgHeld = isNg;

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

        // 图都清了，"保持中"角标也不能留着 —— 否则黑屏上挂着一句"NG 保持中"，
        // 操作员会以为图还在、只是没刷出来。
        IsNgHeld = false;

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
            // 一张都没成功过：这是链路/配置没接通，不是偶发失败，直接给两个动作
            EmptyAction = "切到工程师模式 → 侧边栏「图像来源」，指定平台存图的目录；"
                        + "或在平台流程末尾加「存图」模块并把路径放进报文的 imagePath";
            HasEmptyAction = true;
        }
        else
        {
            EmptyAction = "";
            HasEmptyAction = false;
        }
    }

    // ── 属性变化通知 —— 确保 Tab 切换、图像更新或回看切换时 UI 刷新 ──
    partial void OnSelectedTabChanged(ImageTab value) => OnPropertyChanged(nameof(CurrentImage));
    partial void OnOriginalImageChanged(BitmapSource? value) => OnPropertyChanged(nameof(CurrentImage));
    partial void OnAnnotatedImageChanged(BitmapSource? value) => OnPropertyChanged(nameof(CurrentImage));

    /// <summary>
    /// 回看切换要手动通知 CurrentImage 与按钮文字：
    /// 回看态变化时这两处都变，但它们绑的不是同一个属性，靠一个通知覆盖不到。
    /// </summary>
    partial void OnIsReviewingNgChanged(bool value)
    {
        OnPropertyChanged(nameof(CurrentImage));
        OnPropertyChanged(nameof(NgReviewButtonText));
    }
}

/// <summary>
/// 图像 Tab 标签数据
/// </summary>
/// <param name="Label">显示文字</param>
/// <param name="Key">内部标识</param>
public record ImageTab(string Label, string Key);
