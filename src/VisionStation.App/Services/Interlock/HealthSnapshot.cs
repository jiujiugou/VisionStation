using VisionStation.Service;

namespace VisionStation.App.Services.Interlock;

/// <summary>
/// 启动自检结果的快照 —— AppHost 启动时跑一次自检（F-49），把结果存到这里；
/// 启动联锁（F-47）在运行期读它，不重跑。
///
/// <para>
/// <b>为什么要这个中间人</b>：自检发生在 <c>AppHost.InitializeAsync</c>（组合根），
/// 联锁在 <c>ControlPanelViewModel</c>（界面层），两者没有现成的引用通道。
/// 让 VM 直接依赖 AppHost 会把组合根拽进界面层；一个只读快照的单例是最薄的桥。
/// </para>
///
/// <para>
/// <b>只存不跑</b>：自检的重跑策略归 AppHost（启动期一次），这里绝不提供 "ReRun" ——
/// 联锁读到的一定是"上次启动时的事实"，不是"现在想看的结果"。
/// </para>
/// </summary>
public sealed class HealthSnapshot
{
    private IReadOnlyList<HealthResult> _results = Array.Empty<HealthResult>();

    /// <summary>自检是否已运行过（AppHost 启动路径必然置 true；测试直接构造时是 false）</summary>
    public bool HasRun { get; private set; }

    /// <summary>失败项名称（只留名字，详情在日志里；联锁提示要的是"哪项没过"）</summary>
    public IReadOnlyList<string> FailedItems { get; private set; } = Array.Empty<string>();

    /// <summary>AppHost 自检完成后调用。重复调用以最后一次为准</summary>
    public void Update(IReadOnlyList<HealthResult> results)
    {
        _results = results;
        FailedItems = results.Where(r => !r.OK).Select(r => r.Item).ToArray();
        HasRun = true;
    }
}
