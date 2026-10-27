using System.Net;
using System.Net.Sockets;
using HslCommunication.ModBus;
using VisionStation.App.Services.Inspection;
using VisionStation.Devices;
using VisionStation.Devices.Plc;
using Xunit;

namespace VisionStation.App.Tests;

/// <summary>
/// S2/S3 端到端：被测对象（触发源/回写器）用一个连接，测试用第二个客户端扮演 PLC 侧——
/// 写触发寄存器、读心跳/结果寄存器，全链路真连真读写，不依赖 AutoShop 开着。
///
/// <para>
/// 挂 <see cref="LocalPortCollection"/>：它用 <c>FreePort()</c> 向系统要临时端口，
/// 与同样要端口的测试类并行会抢号（详见那个类的注释）。
/// </para>
/// </summary>
[Collection(LocalPortCollection.Name)]
public class PlcIntegrationTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>轮询直到条件成立或超时（异步事件没有确定的到达时刻，只能等）</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(15);
        }
        Assert.Fail("等待超时：条件在时限内未成立");
    }

    [Fact]
    public async Task PLC置位触发寄存器_软件触发_并回写0自清除()
    {
        var port = FreePort();
        var server = new ModbusTcpServer();
        server.ServerStart(port);
        try
        {
            // PLC 侧（扮演者）：第二个客户端，往触发寄存器写值
            var plcSide = new ModbusTcpNet("127.0.0.1", port, 1);
            Assert.True(plcSide.ConnectServer().IsSuccess);

            // 被测侧：触发源
            var conn = new ModbusTcpPlc("127.0.0.1", port);
            Assert.True(conn.Connect());
            var fired = new TaskCompletionSource();
            using var source = new PlcTriggerSource(conn, ModbusAddressParser.Parse("D200"),
                heartbeatAddress: null, pollIntervalMs: 20);
            source.Triggered += _ => fired.TrySetResult();

            plcSide.Write("200", (short)0);           // 初始 0
            source.Start();

            plcSide.Write("200", (short)1);           // PLC 请求拍照
            await WaitUntilAsync(() => fired.Task.IsCompleted);

            // 自清除：软件必须把触发寄存器写回 0，PLC 只需置位一次
            await WaitUntilAsync(() => plcSide.ReadInt16("200").Content == 0);
        }
        finally
        {
            server.ServerClose();
            server.Dispose();
        }
    }

    [Fact]
    public async Task 触发寄存器保持1不重复触发_回到0再置位才触发第二次()
    {
        var port = FreePort();
        var server = new ModbusTcpServer();
        server.ServerStart(port);
        try
        {
            var plcSide = new ModbusTcpNet("127.0.0.1", port, 1);
            Assert.True(plcSide.ConnectServer().IsSuccess);

            var conn = new ModbusTcpPlc("127.0.0.1", port);
            Assert.True(conn.Connect());
            var count = 0;
            using var source = new PlcTriggerSource(conn, ModbusAddressParser.Parse("D200"),
                null, 20);
            source.Triggered += _ => Interlocked.Increment(ref count);

            source.Start();
            plcSide.Write("200", (short)1);
            await WaitUntilAsync(() => Volatile.Read(ref count) >= 1);

            // 自清除已回写 0；PLC 再置位 → 第二次触发（证明沿检测没有被自清除卡死）
            await WaitUntilAsync(() => plcSide.ReadInt16("200").Content == 0);
            plcSide.Write("200", (short)1);
            await WaitUntilAsync(() => Volatile.Read(ref count) >= 2);
        }
        finally
        {
            server.ServerClose();
            server.Dispose();
        }
    }

    [Fact]
    public async Task 心跳寄存器持续变化_PLC能看出软件活着()
    {
        var port = FreePort();
        var server = new ModbusTcpServer();
        server.ServerStart(port);
        try
        {
            var plcSide = new ModbusTcpNet("127.0.0.1", port, 1);
            Assert.True(plcSide.ConnectServer().IsSuccess);

            var conn = new ModbusTcpPlc("127.0.0.1", port);
            Assert.True(conn.Connect());
            using var source = new PlcTriggerSource(conn, ModbusAddressParser.Parse("D200"),
                ModbusAddressParser.Parse("D205"), 20);

            source.Start();
            var first = plcSide.ReadInt16("205").Content;
            await WaitUntilAsync(() => plcSide.ReadInt16("205").Content != first);
        }
        finally
        {
            server.ServerClose();
            server.Dispose();
        }
    }

    [Fact]
    public async Task 结果回写_OK写1_NG写2_断线不阻塞调用方()
    {
        var port = FreePort();
        var server = new ModbusTcpServer();
        server.ServerStart(port);
        try
        {
            var plcSide = new ModbusTcpNet("127.0.0.1", port, 1);
            Assert.True(plcSide.ConnectServer().IsSuccess);

            var conn = new ModbusTcpPlc("127.0.0.1", port);
            Assert.True(conn.Connect());
            using var writer = new PlcResultWriter(conn, ModbusAddressParser.Parse("D210"));

            writer.Write(isOk: true);
            await WaitUntilAsync(() => plcSide.ReadInt16("210").Content == PlcResultWriter.OkCode);

            writer.Write(isOk: false);
            await WaitUntilAsync(() => plcSide.ReadInt16("210").Content == PlcResultWriter.NgCode);
        }
        finally
        {
            server.ServerClose();
            server.Dispose();
        }
    }

    [Fact]
    public async Task PLC离线_触发源如实上报_设备层带出原因()
    {
        using var conn = new ModbusTcpPlc("127.0.0.1", 1);   // 端口1必有监听，连不上
        using var source = new PlcTriggerSource(conn, ModbusAddressParser.Parse("D200"), null, 50);

        source.Start();
        var device = new TriggerDevice(source);

        Assert.False(source.IsConnected);
        Assert.Equal("PLC离线", device.Status);
        await WaitUntilAsync(() => device.LastError is not null);   // 等首次连接尝试完成
        Assert.Contains("PLC 连接失败", device.LastError);
        // 轮询期间重连尝试不抛异常、不终止 —— Start/Stop 正常收场
        source.Stop();
    }

    /// <summary>
    /// TCP 版掉线恢复：一笔失败之后只要下一笔成功，<c>IsConnected</c> 必须跟着回到 true。
    ///
    /// <para>
    /// 缺陷现场（<c>docs/产线闭环-说明.md</c> §3.1 第 5 项）：<c>Settle</c> 的成功分支不恢复状态，
    /// 于是只跑结果回写、没有轮询的场景里，一次偶发失败就让状态栏永久停在"PLC 离线"，
    /// 而回写其实早就通了 —— 现场有人据此去拔插网线。RTU 版一直是对的，这条用例把两版对齐后的
    /// 行为钉住：失败 → false，成功 → true，<c>LastError</c> 同步清零。
    /// </para>
    ///
    /// <para>
    /// 为什么不用 <see cref="ModbusTcpServer"/>、而用一个只会回显/回异常帧的最小从站：
    /// 这条用例要在<b>同一个 ip:port 上</b>先让一笔失败、再让下一笔成功。
    /// Hsl 的服务器得 <c>ServerClose</c> 再 <c>ServerStart</c> 才能做到，而端口在
    /// TIME_WAIT 里未必绑得回来 —— 那正好是这次要排除的那类随机性（见 <see cref="LocalPortCollection"/>）。
    /// </para>
    /// </summary>
    [Fact]
    public void 一笔失败后下一笔成功_IsConnected恢复true且LastError清零()
    {
        using var slave = new SwitchableModbusSlave();
        using var plc = new ModbusTcpPlc("127.0.0.1", slave.Port);
        var d200 = ModbusAddressParser.Parse("D200");

        Assert.True(plc.Connect(), plc.LastError);
        Assert.True(plc.WriteShort(d200, 1), plc.LastError);
        Assert.True(plc.IsConnected);
        Assert.Null(plc.LastError);

        // PLC 侧开始回 Modbus 异常帧：这一笔必须如实失败，状态翻面（先确认"不会假在线"这条没被破坏）
        slave.RespondWithException = true;
        Assert.False(plc.WriteShort(d200, 2));
        Assert.False(plc.IsConnected);
        Assert.NotNull(plc.LastError);

        // 恢复：下一笔成功必须把状态拉回来。修复前这里会一直停在 false。
        slave.RespondWithException = false;
        Assert.True(plc.WriteShort(d200, 3), plc.LastError);
        Assert.True(plc.IsConnected);
        Assert.Null(plc.LastError);
    }

    /// <summary>
    /// 最小 Modbus TCP 从站替身：一个开关决定"这一笔回异常帧"还是"正常回显"。
    ///
    /// <para>
    /// 只实现写单个寄存器（FC6 / FC16）需要的两种响应 —— 本用例只写不读：
    /// <list type="bullet">
    ///   <item>正常：回 <c>单元标识 + 功能码 + 起始地址 + 寄存器数</c> 共 12 字节（FC6 的成功响应就是原样回显，
    ///         FC16 要再把 MBAP 长度改回 6）；</item>
    ///   <item>异常：回 <c>0x80 | 功能码 + 异常码 0x04</c>，Hsl 会判成失败。</item>
    /// </list>
    /// 用异常帧而不是"连接后立刻关闭"来制造失败，是为了<b>不动 socket</b>：
    /// 这样断言的就纯粹是被测代码的状态收敛，不掺 Hsl 的重连行为。
    /// </para>
    /// </summary>
    private sealed class SwitchableModbusSlave : IDisposable
    {
        /// <summary>异常码 0x04 = 从站设备故障。任意非 0 码 Hsl 都会判失败。</summary>
        private const byte ExceptionCode = 0x04;

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private volatile bool _respondWithException;

        public int Port { get; }

        public SwitchableModbusSlave()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);   // 直接占着，不像 FreePort 那样先放再抢
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        /// <summary>true = 之后每一笔都回异常帧（模拟"PLC 这一笔没成"）；false = 正常回显</summary>
        public bool RespondWithException
        {
            get => _respondWithException;
            set => _respondWithException = value;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return;   // 监听已关或已释放：正常收场
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var request = await ReadFrameAsync(stream);
                        if (request is null) return;

                        if (_respondWithException)
                            await stream.WriteAsync(BuildExceptionResponse(request), _cts.Token);
                        else
                            await stream.WriteAsync(BuildEchoResponse(request), _cts.Token);
                        await stream.FlushAsync(_cts.Token);
                    }
                }
                catch (Exception)
                {
                    // 客户端断开 / 用例结束：没有补救动作
                }
            }
        }

        /// <summary>读一整帧：先读 MBAP 头 6 字节拿长度，再读剩下的（长度含单元标识）。</summary>
        private async Task<byte[]?> ReadFrameAsync(NetworkStream stream)
        {
            var header = new byte[6];
            if (!await ReadExactAsync(stream, header)) return null;

            var frameLength = (header[4] << 8) | header[5];
            if (frameLength <= 0) return null;

            var frame = new byte[6 + frameLength];
            Array.Copy(header, frame, 6);
            return await ReadExactAsync(stream, frame.AsMemory(6)) ? frame : null;
        }

        private async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer[read..], _cts.Token);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        /// <summary>FC6 的成功响应就是请求原样；FC16 要把 MBAP 长度从 9 改回 6。</summary>
        private static byte[] BuildEchoResponse(byte[] request)
        {
            var response = new byte[12];
            Array.Copy(request, response, 12);
            response[4] = 0;
            response[5] = 6;
            return response;
        }

        private static byte[] BuildExceptionResponse(byte[] request) =>
        [
            request[0], request[1],          // 事务号：必须和请求一致，否则客户端配不上这一笔
            0, 0,                            // 协议标识
            0, 3,                            // 后续长度：单元标识 + 功能码 + 异常码
            request[6],                      // 单元标识
            (byte)(request[7] | 0x80),       // 功能码最高位置 1 = 异常响应
            ExceptionCode,
        ];

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
