using System.IO;
using VisionStation.App.Services.Imaging;
using VisionStation.App.ViewModels;
using VisionStation.Core.Models;

namespace VisionStation.App.Tests;

/// <summary>
/// 「图像来源」页的审计契约（F-45：ParameterChange 的唯一真实埋点）。
///
/// 为什么单独立一册：这一页是全程序唯一能改"取图口径"的地方，
/// 而改口径的后果是"后续几小时的图全都找不到" —— 没有流水就无法把
/// "图没了"和"谁在什么时候改了目录"对上。所以这里测的不是保存本身，
/// 是<b>保存有没有留下可复盘的值</b>。
/// </summary>
public class ImageSourcePageViewModelTests
{
    /// <summary>
    /// 改了就记一条 ParameterChange，且 before/after 是具体的值不是"改过了"。
    ///
    /// 断言 After 里带目录名、Before 是"未启用"：只记"修改了图像来源"
    /// 在追责时等于没记 —— 必须能看到从哪改到哪。
    /// </summary>
    [Fact]
    public void Save_WhenValueChanged_RecordsParameterChange()
    {
        var audit = new FakeAuditService();
        var folder = NewTempFolder();
        var vm = new ImageSourcePageViewModel(NewStore(), audit)
        {
            FolderFallbackEnabled = true,
            Folder = folder,
        };

        vm.SaveCommand.Execute(null);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditAction.ParameterChange, entry.Action);
        Assert.Equal("图像来源·目录兜底", entry.Target);
        Assert.Equal("未启用", entry.Before);
        Assert.NotNull(entry.After);
        Assert.Contains(folder, entry.After);
        Assert.Equal(AuditResult.Success, entry.Result);
    }

    /// <summary>
    /// 值没变（连按两次保存）不记流水。
    ///
    /// 审计是流水不是点击计数：重复条目会把"真改过几次"淹掉，
    /// 而现场本来就有人连点两次确认自己点到了。
    /// </summary>
    [Fact]
    public void Save_WhenValueUnchanged_DoesNotRecord()
    {
        var audit = new FakeAuditService();
        var vm = new ImageSourcePageViewModel(NewStore(), audit);

        vm.SaveCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        // 两次保存都是"未启用"，与磁盘上的初始值相同 → 一条都不该记
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// 写盘失败要记 <see cref="AuditResult.Failed"/>，而不是不记。
    ///
    /// 这一条是本类最关键的：页面上"保存失败"的提示只活到下一次点击，
    /// 而"工程师以为改了、实际没生效"是要事后追的（那批图为什么还是旧目录）。
    /// </summary>
    [Fact]
    public void Save_WhenWriteFails_RecordsFailed()
    {
        var audit = new FakeAuditService();
        var folder = NewTempFolder();

        // 用一个"父级是文件"的路径让保存必定抛 IOException：
        // 比造一个假目录权限更稳（不同机器权限不同，测试会时好时坏）。
        var blocker = Path.Combine(Path.GetTempPath(), "vs-imsrc-block-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(blocker, "x");
        var store = new ImageSourceSettingsStore(
            Path.Combine(blocker, "sub", "settings.json"));

        var vm = new ImageSourcePageViewModel(store, audit)
        {
            FolderFallbackEnabled = true,
            Folder = folder,
        };

        vm.SaveCommand.Execute(null);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditAction.ParameterChange, entry.Action);
        Assert.Equal(AuditResult.Failed, entry.Result);
        Assert.False(string.IsNullOrWhiteSpace(entry.Detail));
        Assert.True(vm.StatusIsWarning);
    }

    /// <summary>每次一份新的临时设置文件，用例之间互不影响、也不碰真实配置。</summary>
    private static ImageSourceSettingsStore NewStore()
        => new(Path.Combine(Path.GetTempPath(),
            "vs-imsrc-" + Guid.NewGuid().ToString("N")[..8] + ".json"));

    private static string NewTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(),
            "vs-imsrc-dir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }
}
