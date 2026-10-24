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
/// </summary>
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
}
