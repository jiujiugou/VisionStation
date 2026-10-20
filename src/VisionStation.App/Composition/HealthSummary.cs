using VisionStation.Service;

namespace VisionStation.App.Composition;

/// <summary>
/// 自检结果的界面文案 —— 纯函数，可用假结果直接测，不依赖 WPF。
/// </summary>
public static class HealthSummary
{
    /// <summary>汇总失败的检查项，如「自检 2 项异常: 数据库, 存储路径」</summary>
    public static string Describe(IReadOnlyList<HealthResult> results)
    {
        var failed = new List<string>();
        foreach (var r in results)
        {
            if (!r.OK) failed.Add(r.Item);
        }

        return failed.Count == 0
            ? "自检通过"
            : $"自检 {failed.Count} 项异常: {string.Join(", ", failed)}";
    }
}
