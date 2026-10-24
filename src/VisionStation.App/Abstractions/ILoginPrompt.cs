namespace VisionStation.App.Abstractions;

/// <summary>
/// 弹登录窗的抽象。
///
/// 为什么要有这层：ViewModel 不该自己 <c>new LoginWindow()</c>（那就把 VM 和 WPF 绑死，
/// 一条"没权限时应该弹登录"的测试也就变成"必须创建一个窗口"）。
/// 另外它只暴露一个方法 —— 登录是"一次性问一个问题"，不需要一整套 UI 服务接口。
/// </summary>
public interface ILoginPrompt
{
    /// <summary>
    /// 弹出登录窗。返回 true 表示已登录成功。
    /// </summary>
    /// <param name="reason">
    /// 为什么要登录（显示在窗口里）。比如"切换工程师模式需要工程师权限" ——
    /// 不给原因的话，用户点了按钮突然冒出个登录框，会以为程序出问题了。
    /// </param>
    bool ShowLogin(string? reason = null);
}
