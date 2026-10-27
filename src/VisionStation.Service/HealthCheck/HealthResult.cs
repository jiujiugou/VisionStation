namespace VisionStation.Service.HealthCheck;

/// <summary>
/// 单条自检结果 —— **注意：这是第二份同名类型，当前没有任何生产引用方**。
///
/// <para>
/// 实际在用的是 <c>VisionStation.Service.HealthResult</c>（定义在 <c>HealthChecker.cs</c> 末尾，
/// 字段是 Item / OK / Detail / Error）。这一份（Name / Ok / Message / Duration）是更早的草案，
/// 只因命名空间多了一层 <c>.HealthCheck</c> 才没被误绑 —— 编译得过，行为为零。
/// </para>
///
/// <para>
/// 为什么在注释里点名它而不是删掉：删除属于改代码，本轮只补注释。
/// 但读代码的人看到两个 <c>HealthResult</c> 一定会问"哪个是新的" ——
/// 答案是 <b>要加字段请加到 HealthChecker.cs 那个</b>；改这一份不会有任何界面变化。
/// </para>
///
/// <para>
/// 顺带解释一个查不到的现象：真正的那个结果类型没有耗时字段，
/// 而这里的 <see cref="Duration"/> 从未被任何代码填过 ——
/// 所以"自检里哪一项慢"在当前日志里看不到，只能靠启动总耗时反推。
/// </para>
/// </summary>
public sealed class HealthResult
{
    /// <summary>
    /// 检查项名称（生产实现用的是中文短名："设备连接" / "数据库" / "配方文件" 等，
    /// 测试按这些字符串取项，改名会直接让契约测试挂掉）。
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>是否通过。未通过时 <see cref="Message"/> 必须写清"下一步往哪查"。</summary>
    public bool Ok { get; init; }

    /// <summary>提示信息：通过时是"证据"（如路径、在线数），失败时是"原因"。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>耗时（可选）。当前无写入方，恒为 <c>TimeSpan.Zero</c> —— 见类注释。</summary>
    public TimeSpan Duration { get; init; }
}