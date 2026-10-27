using System.IO;
using VisionStation.App.Services.Imaging;

namespace VisionStation.App.Tests;

/// <summary>
/// <see cref="ImageFileResolver"/> 的规则测试。
///
/// 这里测的不是"能不能读图"，是**允许不允许拿一张图冒充这一件**：
/// 报文只给目录时取最新一张是对的，但取到一张十小时前的图当本件显示，
/// 比黑屏危险得多 —— 现场会拿它当证据。
///
/// 全部用临时目录 + 显式写盘时间，不依赖机器上已有的图片。
/// </summary>
public class ImageFileResolverTests
{
    [Fact]
    public void 报文给完整文件_直接用报文里的()
    {
        using var tmp = new TempFolder();
        var now = DateTime.Now;
        var file = tmp.Create("shot.bmp", now);

        var (path, reason) = ImageFileResolver.Resolve(file, now, ImageSourceSettings.Disabled);

        Assert.Equal(file, path);
        Assert.Null(reason);
    }

    [Fact]
    public void 报文只给目录_取目录里最新的一张()
    {
        using var tmp = new TempFolder();
        var now = DateTime.Now;

        tmp.Create("old.bmp", now.AddSeconds(-8));
        var newest = tmp.Create("new.bmp", now.AddSeconds(-1));

        var (path, reason) = ImageFileResolver.Resolve(tmp.Root, now, ImageSourceSettings.Disabled);

        Assert.Equal(newest, path);
        Assert.Null(reason);
    }

    [Fact]
    public void 目录里的图比本帧旧太多_不拿它冒充本件()
    {
        using var tmp = new TempFolder();
        var now = DateTime.Now;

        tmp.Create("stale.bmp", now.AddMinutes(-10));

        var (path, reason) = ImageFileResolver.Resolve(tmp.Root, now, ImageSourceSettings.Disabled);

        Assert.Null(path);
        Assert.Contains("旧", reason!);
    }

    [Fact]
    public void 报文为空且未启用兜底_说清没配并给出口()
    {
        var (path, reason) = ImageFileResolver.Resolve(null, DateTime.Now, ImageSourceSettings.Disabled);

        Assert.Null(path);
        // 原因里必须带上"去哪改"，否则现场只知道没图、不知道下一步
        Assert.Contains("图像来源", reason!);
    }

    [Fact]
    public void 报文为空但启用兜底_从配置目录取图()
    {
        using var tmp = new TempFolder();
        var now = DateTime.Now;
        var file = tmp.Create("washer_01.bmp", now);

        var settings = new ImageSourceSettings { FolderFallbackEnabled = true, Folder = tmp.Root };

        var (path, reason) = ImageFileResolver.Resolve(null, now, settings);

        Assert.Equal(file, path);
        Assert.Null(reason);
    }

    [Fact]
    public void 兜底目录填了但目录不存在_给出目录名而不是空原因()
    {
        var settings = new ImageSourceSettings
        {
            FolderFallbackEnabled = true,
            Folder = Path.Combine(Path.GetTempPath(), "vs-not-exist-" + Guid.NewGuid().ToString("N")[..8]),
        };

        var (path, reason) = ImageFileResolver.Resolve(null, DateTime.Now, settings);

        Assert.Null(path);
        Assert.Contains("不存在", reason!);
    }

    [Fact]
    public void 不勾递归_子目录里的图取不到()
    {
        using var tmp = new TempFolder();
        var now = DateTime.Now;

        var sub = Directory.CreateDirectory(Path.Combine(tmp.Root, "202610")).FullName;
        var nested = Path.Combine(sub, "nested.bmp");
        File.WriteAllBytes(nested, new byte[16]);
        File.SetLastWriteTime(nested, now);

        var noRecursive = ImageFileResolver.Resolve(tmp.Root, now,
            new ImageSourceSettings { Recursive = false });
        var withRecursive = ImageFileResolver.Resolve(tmp.Root, now,
            new ImageSourceSettings { Recursive = true });

        Assert.Null(noRecursive.Path);
        Assert.Equal(nested, withRecursive.Path);
    }

    /// <summary>临时目录：测试自己造图，不依赖仓库里的素材</summary>
    private sealed class TempFolder : IDisposable
    {
        public string Root { get; }

        public TempFolder()
        {
            Root = Path.Combine(Path.GetTempPath(), "vs-imgres-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Root);
        }

        public string Create(string name, DateTime writeTime)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllBytes(path, new byte[16]);
            File.SetLastWriteTime(path, writeTime);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录删不掉不影响测试结论，留给系统清理
            }
        }
    }
}
