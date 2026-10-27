using System.Windows;
using VisionStation.App.Composition;
using VisionStation.App.ViewModels;

namespace VisionStation.App;

/// <summary>
/// 外壳窗口。它只做三件事：拿 <see cref="ShellViewModel"/> 当 DataContext、
/// 把关闭请求转成宿主能接管的形状、其余交给绑定。
///
/// 改造前它有 12 个构造参数（8 个 View + 4 个 VM），还掌管导航与 F2；
/// 现在它不认识任何面板，也不知道"检测页"长什么样。
/// </summary>
public partial class MainWindow : Window, IAppWindow
{
    /// <summary>自有关闭事件（S2.3）：把 WPF 的 CancelEventArgs 翻译成不依赖 WPF 的 args，见 IAppWindow。</summary>
    private event EventHandler<WindowCloseRequestedEventArgs>? CloseRequested;

    event EventHandler<WindowCloseRequestedEventArgs>? IAppWindow.Closing
    {
        add => CloseRequested += value;
        remove => CloseRequested -= value;
    }

    /// <summary>
    /// 唯一的构造参数是外壳 VM。窗口自己不做任何服务解析 ——
    /// 依赖都已经被 <see cref="ShellViewModel"/> 收拢了，窗口只是它的显示载体。
    /// </summary>
    public MainWindow(ShellViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // S2.3：关闭请求转成自有事件，由 AppHost 决定是否拦截（它会在收尾完成后自行 Shutdown）。
        //
        // 翻译规则只有一句：把宿主的结论抄进 WPF 的 e.Cancel。
        //   宿主没订阅 / 订阅者没设 Cancel → e.Cancel = false → 窗口照常关闭（无宿主时的默认行为）；
        //   宿主设了 Cancel = true          → 窗口这次不关，由宿主走收尾，收尾完再自己 Shutdown。
        // 注意这里**不能**在事件里做 await：WPF 的 Closing 是同步的，异步收尾必须由宿主接管。
        Closing += (_, e) =>
        {
            var args = new WindowCloseRequestedEventArgs();
            CloseRequested?.Invoke(this, args);
            e.Cancel = args.Cancel;
        };
    }
}
