namespace VisionStation.App.Abstractions;

/// <summary>
/// 对话框端口：VM 只表达“需要确认/需要提示”，不直接碰 WPF。
///
/// 为什么返回 Task：
///   生产实现可以是模态 MessageBox，将来也可以换成非模态对话框或自动应答；
///   VM 的命令不必因为实现是同步的就写成同步方法。
/// </summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message);

    Task ShowInfoAsync(string title, string message);

    Task ShowErrorAsync(string title, string message);
}
