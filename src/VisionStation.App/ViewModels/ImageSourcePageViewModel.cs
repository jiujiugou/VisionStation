using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.Services.Imaging;

namespace VisionStation.App.ViewModels;

/// <summary>
/// 「图像来源」页 —— 工程师模式下唯一修改图像来源的入口。
///
/// 为什么放在工程师模式、而不是像以前那样做成"相机页"：
/// 相机现在归视觉平台独占，本程序不再采图，"切换相机"这类操作对产线只有害处。
/// 真正需要现场调的只有一件事：**平台存的图在哪个目录**。
/// 那是配置，不是生产操作 —— 操作员不该看到它，工程师才该。
///
/// 与设置窗（已删除）的区别：页面常驻、能反复改、能看到当前生效状态；
/// 只有一个编辑器，不会出现"两个地方各改一半"。
/// </summary>
public partial class ImageSourcePageViewModel : ObservableObject
{
    private readonly ImageSourceSettingsStore _store;

    /// <summary>是否启用目录兜底</summary>
    [ObservableProperty]
    private bool _folderFallbackEnabled;

    /// <summary>兜底目录</summary>
    [ObservableProperty]
    private string _folder = "";

    /// <summary>是否含子目录（平台按月/日分层时必须勾）</summary>
    [ObservableProperty]
    private bool _recursive = true;

    /// <summary>页面上的提示行：保存结果、测试结果、校验失败的说明都写这里</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>提示行是否为警告语气（红/棕），false 时按普通说明显示</summary>
    [ObservableProperty]
    private bool _statusIsWarning;

    /// <summary>设置文件位置。写出来是为了让人能备份、能拷到另一台机器</summary>
    public string SettingsFilePath => _store.FilePath;

    /// <summary>当前**已生效**的设置摘要（读的是磁盘上那份，不是界面上正在改的值）</summary>
    public string EffectiveText
    {
        get
        {
            var s = _store.Current;
            if (!s.FolderFallbackEnabled) return "未启用 —— 只用报文里的图像路径";

            var suffix = s.Recursive ? "（含子目录）" : "（只看这一层）";
            return string.IsNullOrWhiteSpace(s.Folder)
                ? "已启用，但目录是空的"
                : $"已启用 · {s.Folder}{suffix}";
        }
    }

    public ImageSourcePageViewModel(ImageSourceSettingsStore store)
    {
        _store = store;
        LoadFromStore();
    }

    /// <summary>把磁盘上的设置读进界面（进页面、点「重新读取」时都用它）</summary>
    private void LoadFromStore()
    {
        var current = _store.Current;
        FolderFallbackEnabled = current.FolderFallbackEnabled;
        Folder = current.Folder;
        Recursive = current.Recursive;

        OnPropertyChanged(nameof(EffectiveText));
    }

    [RelayCommand]
    private void Reload()
    {
        LoadFromStore();
        ShowInfo("已重新读取磁盘上的设置。");
    }

    /// <summary>浏览按钮的落点。选目录本身是 UI 行为，由视图调用它把结果交回来。</summary>
    public void ApplyPickedFolder(string folder)
    {
        Folder = folder;
        Probe();
    }

    /// <summary>
    /// 测试：不跑检测，直接看目录里最新一张图。
    ///
    /// 这一步在现场很关键 —— 它把"目录对不对、有没有在写、写的是不是这一件"
    /// 三件事一次看清，不用等平台跑一轮再来猜。
    /// </summary>
    [RelayCommand]
    private void Probe()
    {
        var folder = (Folder ?? "").Trim();

        if (folder.Length == 0)
        {
            ShowWarning("还没有填目录。");
            return;
        }

        if (!Directory.Exists(folder))
        {
            ShowWarning($"目录不存在：{folder}");
            return;
        }

        // frameTime 传 null：这里只想看"最新的是哪张"，不做"是不是这一件的"过期判断
        var (file, _) = ImageFileResolver.NewestIn(folder, Recursive, frameTime: null);

        if (file is null)
        {
            ShowWarning(Recursive
                ? $"目录（含子目录）里没有 bmp/jpg/png 图片：{folder}"
                : $"目录里没有图片（没勾「包含子目录」时只看这一层）：{folder}");
            return;
        }

        var info = new FileInfo(file);
        ShowInfo($"最新一张：{info.Name}\n"
               + $"写入时间 {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}   大小 {info.Length / 1024.0 / 1024.0:0.0} MB\n"
               + $"{info.DirectoryName}");
    }

    [RelayCommand]
    private void Save()
    {
        var enabled = FolderFallbackEnabled;
        var folder = (Folder ?? "").Trim();

        if (enabled && folder.Length == 0)
        {
            ShowWarning("启用兜底时必须填一个目录。");
            return;
        }

        if (enabled && !Directory.Exists(folder))
        {
            // 不硬拦：目录可能是刚建的、盘还没挂上。
            // 但必须说清"现在这个设置暂时不会生效"，别让人以为存了就通了。
            ShowWarning($"目录当前不存在：{folder}\n设置会保存，等目录出现后自动生效。");
        }

        try
        {
            _store.Save(new ImageSourceSettings
            {
                FolderFallbackEnabled = enabled,
                Folder = folder,
                Recursive = Recursive,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存失败必须留在页面上说：静默吞掉会让人以为改好了，下一帧还是旧设置
            ShowWarning($"保存失败：{ex.Message}");
            return;
        }

        OnPropertyChanged(nameof(EffectiveText));
        ShowInfo("已保存，下一帧检测就按新设置取图（不用重启）。");

        WeakReferenceMessenger.Default.Send(new LogMessage(
            $"图像来源已更新：{(enabled ? folder : "未启用")}", LogMsgLevel.Success));
    }

    private void ShowInfo(string text)
    {
        StatusText = text;
        StatusIsWarning = false;
    }

    private void ShowWarning(string text)
    {
        StatusText = text;
        StatusIsWarning = true;
    }
}
