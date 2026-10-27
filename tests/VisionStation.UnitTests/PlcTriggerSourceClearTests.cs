using VisionStation.Devices;
using VisionStation.Devices.Plc;
using Xunit;

namespace VisionStation.UnitTests;

/// <summary>
/// F-17：清位（把 0 写回触发寄存器）失败时，同一次触发不得被处理两次。
///
/// <para>
/// 为什么不能只在 <c>PlcIntegrationTests</c> 里补一条：那组用例跑的是真 ModbusTcpServer，
/// 服务端不会"只让写 0 失败、读照常成功"，模拟不出这个故障。这里用连接替身
/// （<see cref="ScriptedPlc"/>）精确控制"第几次清位开始失败"，把两条时序都钉住：
/// <list type="number">
///   <item>清位失败 → 状态保持"欠一次清位"，下一轮**重试清位**而不是当成新沿；</item>
///   <item>重试成功后（或 PLC 自己把位回 0）状态复位，下一次置位仍能正常触发 —— 防"清位一直失败就永远不触发"。</item>
/// </list>
/// </para>
///
/// <para>
/// 生产接口没有被改动：替身只实现 <see cref="IPlcConnection"/>，靠返回 false 表达写失败，
/// 这正是真实实现在断线时的行为。
/// </para>
/// </summary>
public class PlcTriggerSourceClearTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, string message, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.True(condition(), $"{message}（等了 {timeoutMs}ms）");
    }

    [Fact]
    public async Task 清位失败不重复触发_重试清位后下一次置位仍能触发()
    {
        // 前 6 次"写 0"全部失败：足够让轮询跑过好几个周期，能看出会不会重复触发
        var plc = new ScriptedPlc(failClearAttempts: 6);
        var triggers = 0;

        using var source = new PlcTriggerSource(plc, ModbusAddressParser.Parse("D100"),
            heartbeatAddress: null, pollIntervalMs: 20);
        source.Triggered += _ => Interlocked.Increment(ref triggers);

        source.Start();
        plc.PlcWritesTrigger(1);            // PLC 置位：有料

        // 第一次触发必须发生 —— 清位失败不等于这一件可以丢掉（丢了 PLC 会等一个永远不来的结果）
        await WaitUntilAsync(() => Volatile.Read(ref triggers) >= 1, "清位失败时第一次触发没有发生");

        // 至少真的失败过一次清位，否则后面的断言没验到东西
        await WaitUntilAsync(() => plc.FailedClears >= 1, "没有观察到清位失败");
        Assert.Equal(1, Volatile.Read(ref triggers));   // ★ F-17 的核心断言：失败当场不得再触发

        // 失败次数用完之后，靠"重试清位"自己把 D100 清回 0（PLC 侧没有写过 0）
        await WaitUntilAsync(() => plc.TriggerValue == 0, "重试清位始终没有成功：清位失败后卡死");

        // 整个过程只处理了一次（旧代码这里会是 2 次以上）
        Assert.Equal(1, Volatile.Read(ref triggers));

        // 不卡死：PLC 重新置位，仍应正常触发第二次
        plc.PlcWritesTrigger(1);
        await WaitUntilAsync(() => Volatile.Read(ref triggers) >= 2, "重试清位之后新的上升沿没有被识别");
    }

    /// <summary>
    /// PLC 连接替身：一个触发寄存器（D100）的语义 + 可控的清位失败次数。
    ///
    /// 加锁的理由与生产实现一致：读发生在轮询线程、测试线程也会读写同一批字段。
    /// </summary>
    private sealed class ScriptedPlc : IPlcConnection
    {
        private readonly object _sync = new();
        private short _trigger;              // 触发寄存器当前值（PLC 侧视角）
        private int _failClearsLeft;         // 还要让几次"写 0"失败
        private int _failedClears;

        /// <param name="failClearAttempts">前 N 次清位尝试返回 false（模拟通信失败），之后恢复正常</param>
        public ScriptedPlc(int failClearAttempts)
        {
            _failClearsLeft = failClearAttempts;
        }

        public bool IsConnected => true;

        public string? LastError { get; private set; }

        public bool Connect() => true;

        public void Disconnect() { }

        public short? ReadShort(PlcAddress address)
        {
            lock (_sync) return _trigger;
        }

        public bool? ReadBit(PlcAddress address) => false;

        public bool WriteShort(PlcAddress address, short value)
        {
            lock (_sync)
            {
                if (value != 0)
                {
                    // 本用例只写清位值 0（心跳地址传了 null，不会走到这里）
                    LastError = "测试替身只接受清位写入";
                    return false;
                }

                if (_failClearsLeft > 0)
                {
                    _failClearsLeft--;
                    _failedClears++;
                    LastError = "模拟清位写失败（通信中断）";
                    return false;   // 关键：失败**不改** _trigger，PLC 侧 D100 还是 1
                }

                _trigger = 0;
                LastError = null;
                return true;
            }
        }

        public bool WriteBit(PlcAddress address, bool value) => true;

        /// <summary>PLC 侧写触发寄存器（1 = 有料）</summary>
        public void PlcWritesTrigger(short value)
        {
            lock (_sync) _trigger = value;
        }

        public short TriggerValue
        {
            get { lock (_sync) return _trigger; }
        }

        /// <summary>已经失败过几次清位（断言"故障真的被触发过"，避免用例空转成绿）</summary>
        public int FailedClears
        {
            get { lock (_sync) return _failedClears; }
        }
    }
}
