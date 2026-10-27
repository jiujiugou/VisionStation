using NetArchTest.Rules;
using VisionStation.Core.Engines;
using VisionStation.Service;

namespace VisionStation.UnitTests;

/// <summary>
/// 架构约束。只放编译器管不了的规则。
///
/// 这里刻意不写的东西（因为已经由更硬的东西保证，写了就是噪音）：
///   - Core 不依赖 Storage / Devices —— Core.csproj 没那个 ProjectReference
///   - Core 不引用 System.Windows —— Core 是 net8.0，根本编不过
///   - App 不被任何项目引用 —— App 是 net8.0-windows，其他是 net8.0
///   - 触发源必须可释放 —— ITriggerSource 已继承 IDisposable，
///     类型系统强制，漏写编不过（2026-10-19 从测试下沉到接口）
///     （相机层 2026-10-26 已删除：相机归视觉平台独占，本程序不采图）
///
/// 曾经写过的两条规则已删除，记在这里防止有人加回来：
///   - 「Storage 不许依赖 OpenCvSharp」是错的。存储层要把 Mat 编码成 jpg，
///     图像序列化本来属于存储职责。它真正暴露的债（Core 公开 API 带 Mat）
///     NetArchTest 查不出来，只能靠人 —— 见 AGENTS.md 已知坑 15。
///
/// 能放这里的只剩一类：第三方库的层边界（编译器只查"有没有这个包"，
/// 不查"这一层该不该用这个包"）。
/// </summary>
public class ArchitectureTests
{
    private static readonly System.Reflection.Assembly CoreAssembly = typeof(IInspectionEngine).Assembly;
    private static readonly System.Reflection.Assembly ServiceAssembly = typeof(HealthChecker).Assembly;

    [Fact]
    public void Service_不应依赖图像处理库()
    {
        // 服务层做健康检查与清理，没有任何理由需要 OpenCV
        var result = Types.InAssembly(ServiceAssembly)
            .ShouldNot()
            .HaveDependencyOn("OpenCvSharp")
            .GetResult();

        AssertPass(result, "服务层只做健康检查与清理，不该碰图像");
    }

    [Fact]
    public void Core_不应依赖持久化实现()
    {
        // 领域层不该知道 EF Core 的存在；持久化实现属于 Storage
        var result = Types.InAssembly(CoreAssembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        AssertPass(result, "领域层不碰持久化实现");
    }

    /// <summary>
    /// F-10（2026-10-27 拍板）：不按引擎"能力"分流。
    ///
    /// 曾经的 <c>IPushDrivenEngine</c> 空接口只在控制面板弹一句提示，
    /// 真正的执行路径（<c>RunOnceAsync</c>）压根不看它 —— 结论对、理由不对。
    /// 单引擎下没有第二种取值可对照，它已删除；这条测试防它被人加回来。
    /// </summary>
    [Fact]
    public void 引擎契约里不应再有推驱动能力标记()
    {
        var marker = CoreAssembly.GetType(
            "VisionStation.Core.Engines.IPushDrivenEngine", throwOnError: false);

        Assert.Null(marker);
    }

    private static void AssertPass(TestResult result, string reason)
    {
        if (result.IsSuccessful)
        {
            return;
        }

        var failing = result.FailingTypeNames is null
            ? "(未返回失败类型)"
            : string.Join(", ", result.FailingTypeNames);

        Assert.Fail($"架构规则被破坏：{reason}。违规类型：{failing}");
    }
}
