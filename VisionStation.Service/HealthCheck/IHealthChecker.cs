namespace VisionStation.Service;

/// <summary>
/// 健康检查接口 —— 开机自检
/// </summary>
public interface IHealthChecker
{
    List<HealthResult> RunAll();
}
