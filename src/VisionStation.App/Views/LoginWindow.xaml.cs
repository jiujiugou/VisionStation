using System.Windows;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

/// <summary>
/// 登录窗（模态）。
///
/// 它由用户主动触发（点顶栏的"登录"/点需要权限的按钮），**不在启动流程里弹**。
/// 这是刻意的：启动就弹模态框，如果现场无人值守，进程会卡在对话框上，
/// 看门狗的 WaitForExit 永远等不到退出信号 —— 启动路径上那条注释说的正是这个坑。
/// 未登录状态本身是合法的：能看、不能动。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly LoginViewModel _viewModel;

    /// <summary>
    /// 构造：接管 DataContext、订阅"登录完成"、把焦点放到用户名框。
    ///
    /// <para>
    /// 用的是构造注入而不是从容器解析：这个窗口每次登录都新建一份，
    /// 由 <c>WpfLoginPrompt</c> 负责 new 并决定归属（Owner）。
    /// </para>
    /// </summary>
    public LoginWindow(LoginViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Completed += OnCompleted;

        Loaded += (_, _) =>
        {
            UserNameBox.Focus();
            UserNameBox.SelectAll();
        };
    }

    /// <summary>
    /// VM 说"登录成功 / 取消" → 关窗并回传结果。
    /// <c>ShowDialog()</c> 的返回值就是这里的 <c>DialogResult</c>。
    /// </summary>
    private void OnCompleted(object? sender, bool success)
    {
        // Esc / Alt+F4 可能已经把窗关了，此时再设 DialogResult 会抛
        try { DialogResult = success; }
        catch (InvalidOperationException) { /* 窗口已关闭，忽略 */ }
    }

    /// <summary>
    /// 登录 / 改密按钮。声明成 <c>async void</c> 是事件处理器的标准形态 ——
    /// 异常已经在 <see cref="LoginViewModel.SubmitAsync"/> 内部转成了 <c>ErrorText</c>，
    /// 不会逃逸到这个方法外面。
    /// </summary>
    private async void OnSubmitClick(object sender, RoutedEventArgs e)
    {
        await SubmitAsync();
    }

    /// <summary>
    /// 提交后按模式切换界面上那两块面板（登录区 / 改密区）。
    ///
    /// <para>
    /// 为什么切面板放在视图里：它是纯显示切换，用的是 <c>Visibility</c>；
    /// VM 只负责说"现在要改密了"（<c>IsChangePasswordMode</c>），不关心界面怎么摆。
    /// </para>
    /// </summary>
    private async Task SubmitAsync()
    {
        // 三个密码框都读出来传进去，至于用哪个由 ViewModel 按模式决定：
        // 改密模式下 PasswordInput 里存的是"当前密码"，正是改密需要的那一个。
        await _viewModel.SubmitAsync(
            PasswordInput.Password,
            NewPasswordInput.Password,
            ConfirmPasswordInput.Password);

        if (_viewModel.IsChangePasswordMode && ChangePanel.Visibility != Visibility.Visible)
        {
            LoginPanel.Visibility = Visibility.Collapsed;
            ChangePanel.Visibility = Visibility.Visible;

            // 焦点给新密码框：这一步之后用户不用再回头找光标在哪
            NewPasswordInput.Focus();
        }
    }

    /// <summary>取消：通知 VM 回传 false，窗口随即关闭，不建立会话。</summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _viewModel.Cancel();
    }
}
