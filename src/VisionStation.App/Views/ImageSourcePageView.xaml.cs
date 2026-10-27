using System.IO;
using System.Windows;
using VisionStation.App.ViewModels;

namespace VisionStation.App.Views;

/// <summary>
/// 「图像来源」页。
///
/// 只有一件事在视图里做：选目录。文件对话框是 WPF 的东西，ViewModel 不该
/// <c>new OpenFolderDialog()</c> —— 所以这里选完把路径交回 VM，校验和保存仍在 VM。
/// </summary>
public partial class ImageSourcePageView
{
    public ImageSourcePageView() => InitializeComponent();

    /// <summary>
    /// 打开"选择文件夹"对话框，选完把路径交回 VM。
    ///
    /// <para>
    /// 这里只做选择，**不做校验也不保存**：VM 选完会立刻 Probe 一次（看目录里有没有图），
    /// 是否落到磁盘要等用户点「保存」。这样"选错目录"可以直接再选，不会留下半保存的状态。
    /// </para>
    /// </summary>
    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ImageSourcePageViewModel viewModel) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择平台存图的目录",
            // 当前填的目录存在就直接定位过去，省掉一层层展开
            InitialDirectory = Directory.Exists(viewModel.Folder) ? viewModel.Folder : "",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            viewModel.ApplyPickedFolder(dialog.FolderName);
    }
}
