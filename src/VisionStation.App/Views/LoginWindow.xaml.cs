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

    private void OnCompleted(object? sender, bool success)
    {
        // Esc / Alt+F4 可能已经把窗关了，此时再设 DialogResult 会抛
        try { DialogResult = success; }
        catch (InvalidOperationException) { /* 窗口已关闭，忽略 */ }
    }

    private async void OnSubmitClick(object sender, RoutedEventArgs e)
    {
        await SubmitAsync();
    }

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

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _viewModel.Cancel();
    }
}
