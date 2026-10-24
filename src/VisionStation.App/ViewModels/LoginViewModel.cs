using CommunityToolkit.Mvvm.ComponentModel;
using VisionStation.Core.Users;
using VisionStation.Service.Security;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 登录窗 ViewModel。
///
/// 两个模式共用一个窗口：
///   登录模式 —— 用户名 + 密码 + 登录
///   改密模式 —— 新密码 + 确认（出厂密码或管理员重置后强制走这一步）
///
/// 密码为什么不作为属性：<c>PasswordBox.Password</c> 本来就不该进 ViewModel。
/// 属性会留在内存里、会被绑定系统持有、还会出现在任何一次对象转储里。
/// 这里改成方法参数 —— 从控件读出来直接交给服务，用完即弃。
/// </summary>
public partial class LoginViewModel : ObservableObject
{
    private readonly IUserService _users;
    private readonly ISessionService _session;

    [ObservableProperty]
    private string _userName = string.Empty;

    /// <summary>错误提示（登录失败 / 改密失败）</summary>
    [ObservableProperty]
    private string _errorText = string.Empty;

    /// <summary>是否正在处理（禁用按钮，避免连点产生多次失败计数）</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>true = 改密模式</summary>
    [ObservableProperty]
    private bool _isChangePasswordMode;

    /// <summary>改密模式下提示"为什么要求改密"</summary>
    [ObservableProperty]
    private string _changePasswordHint = string.Empty;

    /// <summary>验证通过后的账号；窗口据此判断登录是否真的成功了</summary>
    public UserAccount? AuthenticatedUser { get; private set; }

    public string StationName => "金属零件视觉检测工位";

    /// <summary>主按钮文字随模式变</summary>
    public string SubmitButtonText => IsChangePasswordMode ? "修改并进入" : "登录";

    partial void OnIsChangePasswordModeChanged(bool value)
        => OnPropertyChanged(nameof(SubmitButtonText));

    /// <summary>窗口订阅它：参数为 true 表示已登录，可以关闭并继续</summary>
    public event EventHandler<bool>? Completed;

    public LoginViewModel(IUserService users, ISessionService session)
    {
        _users = users;
        _session = session;
    }

    /// <summary>
    /// 提交。三个密码参数由窗口从 PasswordBox 读出后传入。
    /// </summary>
    public async Task SubmitAsync(string password, string newPassword = "", string confirmPassword = "")
    {
        if (IsBusy) return;

        IsBusy = true;
        ErrorText = string.Empty;

        try
        {
            if (IsChangePasswordMode)
                await SubmitChangePasswordAsync(password, newPassword, confirmPassword);
            else
                await SubmitLoginAsync(password);
        }
        catch (Exception ex)
        {
            // 认证路径上的意外：给操作员看得懂的一句话，细节留给日志
            ErrorText = $"登录过程出错：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SubmitLoginAsync(string password)
    {
        if (string.IsNullOrWhiteSpace(UserName))
        {
            ErrorText = "请输入用户名";
            return;
        }

        var result = await _users.AuthenticateAsync(UserName.Trim(), password);

        if (!result.Succeeded || result.User is null)
        {
            ErrorText = result.Message;
            return;
        }

        // 出厂密码 / 管理员重置后：先改密，不建立会话。
        // 不建立会话是刻意的 —— 用出厂密码登录进来哪怕只有一次，
        // 也等于让"谁都能进去"这件事发生过。
        if (result.User.MustChangePassword)
        {
            ChangePasswordHint = "当前使用的是出厂密码（或已被管理员重置），必须修改后才能继续";
            IsChangePasswordMode = true;
            ErrorText = string.Empty;
            return;
        }

        Establish(result.User);
    }

    private async Task SubmitChangePasswordAsync(string currentPassword, string newPassword, string confirmPassword)
    {
        if (string.IsNullOrEmpty(newPassword))
        {
            ErrorText = "请输入新密码";
            return;
        }

        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            ErrorText = "两次输入的新密码不一致";
            return;
        }

        var (ok, error) = await _users.ChangePasswordAsync(UserName.Trim(), currentPassword, newPassword);
        if (!ok)
        {
            ErrorText = error ?? "修改密码失败";
            return;
        }

        // 改完立刻用新密码重新认证一次：拿到的是更新后的账号对象（MustChangePassword 已为 false）
        var result = await _users.AuthenticateAsync(UserName.Trim(), newPassword);
        if (!result.Succeeded || result.User is null)
        {
            // 改密成功但重认证失败：极端情况（比如刚好被停用），如实报出来
            ErrorText = $"密码已修改，但重新登录失败：{result.Message}";
            IsChangePasswordMode = false;
            return;
        }

        Establish(result.User);
    }

    private void Establish(UserAccount user)
    {
        AuthenticatedUser = user;
        _session.Start(user);
        Completed?.Invoke(this, true);
    }

    /// <summary>取消：窗口据此关闭且不建立会话</summary>
    public void Cancel() => Completed?.Invoke(this, false);
}
