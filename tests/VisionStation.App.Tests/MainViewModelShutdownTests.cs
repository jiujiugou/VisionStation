using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using VisionStation.App.Composition.Lifecycle;
using VisionStation.App.ViewModels;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;
using VisionStation.Storage;

namespace VisionStation.App.Tests;

/// <summary>
/// L1 证据：C2 —— 关闭开始后不再有新的检测帧。
///
/// 这条测试同时钉住了一个容易踩的实现顺序：拦截必须发生在
/// <c>Application.Current.Dispatcher</c> 之前。测试环境没有 Application 实例，
/// 如果实现先切线程再判断"是否正在关闭"，这里会直接 NullReferenceException。
/// </summary>
public class MainViewModelShutdownTests
{
    [Fact]
    public void 关闭开始后_触发不再进入采图()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisionStation.ShutdownTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var trigger = new FakeTriggerSource();
            var camera = new FakeCamera();
            var lifetime = new AppLifetime();
            var monitor = new InFlightWorkMonitor();

            var vm = new MainViewModel(
                trigger,
                camera,
                new Pipeline(Array.Empty<IVisionTool>()),   // 关闭测试不需要任何工具
                new FakeInspectionRepository(),
                new ImageStoreService(root),
                new CalibrationConfig(),
                NullLogger<MainViewModel>.Instance,
                new ImagePanelViewModel(),
                new StatusBarViewModel(),
                new RecipeConfig(),
                lifetime,
                monitor);

            lifetime.BeginStop();
            trigger.Fire();

            // 采图次数为 0 = 这一帧根本没开始；在途计数为 0 = 也没有残留租约
            Assert.Equal(0, camera.CaptureCount);
            Assert.Equal(0, monitor.Count);

            vm.Dispose();
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 临时目录清理失败不影响断言 */ }
        }
    }

    // 注意：未关闭时的触发路径依赖 Application.Current.Dispatcher，App.Tests 不创建 Application，
    // 因此"正常触发 → 采图一次 → 租约归零"这一条在本层不可测，记为 NOT_RUN。
    // 它的证据来自 L3 走查（自动运行中关窗，日志出现 wait-idle ok）。
}
