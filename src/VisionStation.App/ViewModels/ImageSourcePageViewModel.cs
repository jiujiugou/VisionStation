using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using VisionStation.App.Services.Imaging;
using VisionStation.Core.Models;
using VisionStation.Service.Audit;

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
    /// <summary>
    /// 设置的持久化。注意本类**不缓存**设置：每次读写都走它，
    /// 因为它内部维护着"当前生效值"，两边各存一份就会出现"界面显示的和实际用的不一样"。
    /// </summary>
    private readonly ImageSourceSettingsStore _store;

    /// <summary>
    /// 审计。本页是「取图口径」的唯一改动入口，改完必须留痕 ——
    /// 事后查"那批图为啥路径变了"只能靠这条流水对上时间。
    /// </summary>
    private readonly IAuditService _audit;

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

    /// <summary>
    /// 构造即把磁盘上的设置读进界面。
    ///
    /// <para>
    /// 页面本身是单例（容器注册为 Singleton），所以这个构造只发生一次；
    /// 之后要重新同步靠 <see cref="Reload"/>。切换页面**不会**重新读 —— 这是刻意的：
    /// 工程师正在改一半时切走再切回，不该把他没保存的输入冲掉。
    /// </para>
    /// </summary>
    public ImageSourcePageViewModel(ImageSourceSettingsStore store, IAuditService audit)
    {
        _store = store;
        _audit = audit;
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

    /// <summary>
    /// 丢弃界面上的改动，重新从磁盘读。
    /// 用于"改了但没保存，想反悔"和"另一个进程改过这份文件"两种情况。
    /// </summary>
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

    /// <summary>
    /// 保存到磁盘。校验顺序是刻意的：**先拦"启用但没填目录"，再放行"目录暂时不存在"**。
    ///
    /// <para>
    /// 目录不存在只警告不拦，是因为现场常见"网络盘还没挂上"和"目录还没建"。
    /// 硬拦会逼着工程师先切到资源管理器建目录再回来，而他此刻可能正在远程桌面里。
    /// 代价是必须把"现在这条设置暂时不生效"说清楚。
    /// </para>
    /// <para>
    /// 保存成功后广播一条日志：改图像来源属于"改了取图口径"，
    /// 事后查"为什么那批图的路径变了"要能对上时间。
    /// </para>
    /// <para>
    /// <b>审计埋点（ParameterChange）</b>：本页是全程序唯一能改"取图口径"的地方，
    /// 也就成了 <see cref="AuditAction.ParameterChange"/> 唯一真实的调用点。
    /// 成功记 before/after 的<b>具体值</b>，失败记 <see cref="AuditResult.Failed"/>——
    /// 只记成功会把"改了却没生效"这段最该查的历史抹掉。
    /// </para>
    /// </summary>
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

        var next = new ImageSourceSettings
        {
            FolderFallbackEnabled = enabled,
            Folder = folder,
            Recursive = Recursive,
        };

        // 改前的值必须从 _store 取（磁盘上那份"正在生效"的），不能用界面上的输入 ——
        // 界面可能已经被改过好几轮却没保存，拿它当 before 会记出一个从未生效过的"旧值"。
        var previous = _store.Current;

        try
        {
            _store.Save(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存失败必须留在页面上说：静默吞掉会让人以为改好了，下一帧还是旧设置
            ShowWarning($"保存失败：{ex.Message}");

            // 失败也要记：审计三态里的 Failed 正是"按了但没成"，
            // 只记成功等于把"改了却没生效"这段最该查的历史抹掉。
            _audit.Record(AuditAction.ParameterChange, ParameterTarget,
                before: Describe(previous), after: Describe(next),
                result: AuditResult.Failed, detail: ex.Message);
            return;
        }

        // 埋点放在「确实写盘成功之后」：写失败已经在上面的 catch 里记成 Failed，
        // 这里再记一条 Success 会把同一动作写成两条互相矛盾的记录。
        // 值没变（连按两次保存）不记 —— 审计是流水，不是点击计数。
        if (previous != next)
        {
            _audit.Record(AuditAction.ParameterChange, ParameterTarget,
                before: Describe(previous), after: Describe(next),
                detail: "图像来源设置已保存");
        }

        OnPropertyChanged(nameof(EffectiveText));
        ShowInfo("已保存，下一帧检测就按新设置取图（不用重启）。");

        WeakReferenceMessenger.Default.Send(new LogMessage(
            $"图像来源已更新：{(enabled ? folder : "未启用")}", LogMsgLevel.Success));
    }

    /// <summary>
    /// 审计里的操作对象名。写死常量而不是拼路径 ——
    /// 审计页按 Target 检索时，人和机器都只会用同一个词找这条流水。
    /// </summary>
    private const string ParameterTarget = "图像来源·目录兜底";

    /// <summary>
    /// 把设置转成审计里的 before/after 文本。
    ///
    /// 记的是<b>值</b>不是"改过了"：只写"修改了图像来源"，事后无法回答
    /// "那批图为什么找不到了"——必须看到目录从哪换到了哪、递归有没有被关掉。
    /// </summary>
    private static string Describe(ImageSourceSettings settings)
    {
        if (!settings.FolderFallbackEnabled) return "未启用";

        var folder = string.IsNullOrWhiteSpace(settings.Folder) ? "（目录为空）" : settings.Folder;
        return $"启用·{folder}{(settings.Recursive ? "（含子目录）" : "（只看这一层）")}";
    }

    /// <summary>提示行：普通说明（蓝/灰），例如"已保存""最新一张是…"。</summary>
    private void ShowInfo(string text)
    {
        StatusText = text;
        StatusIsWarning = false;
    }

    /// <summary>提示行：警告语气（红/棕），用于校验失败与保存失败。**不要用它报成功**。</summary>
    private void ShowWarning(string text)
    {
        StatusText = text;
        StatusIsWarning = true;
    }
}
