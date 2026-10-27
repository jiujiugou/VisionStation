using Xunit;

namespace VisionStation.App.Tests;

/// <summary>
/// 「占用本机 TCP 端口」的测试集合 —— 同集合内串行执行。
///
/// <para>
/// 为什么需要它：这些测试都靠 <c>new TcpListener(IPAddress.Loopback, 0)</c> 向系统要一个
/// 空闲端口，拿到号之后**先释放、再重新绑定**（<c>FreePort()</c> 的写法）。这中间有个空窗，
/// 另一个并行跑的测试类完全可能把同一个端口要走：先释放的那个再 bind 就报"地址已在使用"，
/// 表现为**随机一条**集成测试失败，重跑又过 —— 最容易被当成"偶发 flaky"糊过去。
///
/// <para>
/// 2026-10-27 实测撞过：<see cref="PlcIntegrationTests"/> 的
/// <c>触发寄存器保持1不重复触发...</c> 在全量跑时红、单跑时绿。根因不是被测代码，
/// 是 <see cref="HealthRuntimeProbeTests"/> 并行占端口。
///
/// <para>
/// 所以凡是本机起监听/连本机端口的测试类，都挂这个 Collection。别为了跑得快把
/// <c>DisableParallelization</c> 去掉 —— 快几秒换来一条不可信的随机红灯不划算。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalPortCollection
{
    /// <summary>集合名。挂 <c>[Collection(LocalPortCollection.Name)]</c> 即加入。</summary>
    public const string Name = "本机 TCP 端口";
}
