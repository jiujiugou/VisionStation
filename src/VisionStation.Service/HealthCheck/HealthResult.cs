namespace VisionStation.Service.HealthCheck;

public sealed class HealthResult
{
    /// <summary>
    /// 检查项名称
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 是否通过
    /// </summary>
    public bool Ok { get; init; }

    /// <summary>
    /// 提示信息
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// 耗时（可选）
    /// </summary>
    public TimeSpan Duration { get; init; }
}