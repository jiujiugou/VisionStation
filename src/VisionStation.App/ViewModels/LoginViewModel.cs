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
    /// <summary>账号服务。负责认证（含失败锁定计数）与改密，本类不自己比对密码。</summary>
    private readonly IUserService _users;

    /// <summary>会话。只有认证成功后才会 <c>Start</c>；改密模式下刻意不碰它。</summary>
    private readonly ISessionService _session;

    /// <summary>
    /// 用户名。**会在界面上明文显示**，所以没有理由不进 VM；
    /// 密码则相反，见类型注释。
    /// </summary>
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

    /// <summary>窗口标题里显示的工位名。与顶栏同源（同一个常量语义），改名字要两处一起改。</summary>
    public string StationName => "金属零件视觉检测工位";

    /// <summary>主按钮文字随模式变</summary>
    public string SubmitButtonText => IsChangePasswordMode ? "修改并进入" : "登录";

    /// <summary>模式切换 → 主按钮文字跟着变（"登录" / "修改并进入"）。</summary>
    partial void OnIsChangePasswordModeChanged(bool value)
        => OnPropertyChanged(nameof(SubmitButtonText));

    /// <summary>窗口订阅它：参数为 true 表示已登录，可以关闭并继续</summary>
    public event EventHandler<bool>? Completed;

    /// <summary>
    /// 构造。这里**不读会话、不回填用户名** —— 每次登录都应该是干净的一张表单
    /// （见 <c>WpfLoginPrompt</c> 用 Transient 注册）。
    /// </summary>
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

    /// <summary>
    /// 登录模式：校验非空 → 交给账号服务认证 → 需要改密就切模式 → 否则建立会话。
    ///
    /// <para>
    /// 注意"认证成功"与"登录完成"是两件事：<c>MustChangePassword</c> 为 true 时
    /// 认证通过但不建会话，必须先改密。中间那个 <c>ChangePasswordHint</c>
    /// 就是告诉用户"为什么要再填一次"。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 改密模式：两次输入必须一致 → 调服务改密 → **用新密码重新认证一次**。
    ///
    /// <para>
    /// 为什么要重新认证而不是直接用旧的对象：改密成功后账号对象的
    /// <c>MustChangePassword</c> 已经变成 false，但手上这份是改密**之前**的快照。
    /// 重新认证拿到的才是最新状态，也顺便验证了"新密码确实能用"。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 认证通过后的唯一收口：记下账号 → 启动会话 → 通知窗口"成功"。
    ///
    /// <para>
    /// 三步放在一起，是因为它们必须同时发生：只 Start 不通知窗口，用户会看到一个
    /// 已经登录成功却关不掉的登录框；只通知窗口不 Start，进去之后没有任何权限。
    /// </para>
    /// </summary>
    private void Establish(UserAccount user)
    {
        AuthenticatedUser = user;
        _session.Start(user);
        Completed?.Invoke(this, true);
    }

    /// <summary>取消：窗口据此关闭且不建立会话</summary>
    public void Cancel() => Completed?.Invoke(this, false);
}
