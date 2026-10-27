using System.Windows;
using System.Windows.Controls;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

/// <summary>
/// 用户管理页视图。
///
/// <para>
/// 只有三类事在 code-behind 里做：首次显示时拉一次列表、把两个 <see cref="PasswordBox"/>
/// 里的明文交给 ViewModel、成功后清掉框里的圆点。
/// 校验、权限复核、审计一律在 VM —— 这里不许出现"这个角色能不能改"这类判断。
/// </para>
///
/// <para>
/// <b>为什么密码要靠 code-behind 传</b>：<see cref="PasswordBox.Password"/> 不是依赖属性，
/// 绑不上（这是 WPF 有意的设计，避免明文被绑定引擎缓存）。登录窗用的是同一套做法 ——
/// 点按钮那一刻取一次，交给 VM 后立刻不留第二份。
/// </para>
/// </summary>
public partial class UserManagePageView : UserControl
{
    public UserManagePageView()
    {
        InitializeComponent();

        // 首次显示时查一次。放 Loaded 而不是构造函数：VM 是单例，构造发生在启动组装阶段，
        // 那时查库会拖慢冷启动；而且用户可能整班都不打开这一页。
        // 列表非空就不再查 —— 每次切页重查会让人刚选中的行消失。
        Loaded += (_, _) =>
        {
            if (DataContext is UserManagePageViewModel { Users.Count: 0 } viewModel)
                viewModel.RefreshCommand.Execute(null);
        };
    }

    /// <summary>
    /// 新建账号。声明成 <c>async void</c> 是事件处理器的标准形态（与登录窗一致）；
    /// 命令内部已经把业务失败和异常都翻译成提示行，这里只负责把密码递进去。
    /// </summary>
    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UserManagePageViewModel viewModel) return;

        viewModel.NewUserPassword = NewPasswordBox.Password;
        await viewModel.CreateCommand.ExecuteAsync(null);

        // VM 成功后会把属性清空。空 = 成功，此时清掉框里的圆点，
        // 否则下一次点"新建"会带着上一轮的旧密码，而界面上看起来"什么都没填"。
        if (viewModel.NewUserPassword.Length == 0)
            NewPasswordBox.Clear();
    }

    /// <summary>重置选中账号的密码。同上：密码在点击这一刻才从 PasswordBox 取出。</summary>
    private async void OnResetPasswordClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UserManagePageViewModel viewModel) return;

        viewModel.ResetPassword = ResetPasswordBox.Password;
        await viewModel.ResetPasswordCommand.ExecuteAsync(null);

        if (viewModel.ResetPassword.Length == 0)
            ResetPasswordBox.Clear();
    }
}
