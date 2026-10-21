using System.Net;
using System.Net.Sockets;
using HslCommunication.ModBus;
using VisionStation.Devices.Plc;
using Xunit;

namespace VisionStation.UnitTests;

public class ModbusAddressParserTests
{
    [Theory]
    [InlineData("D100", ModbusArea.HoldingRegister, 100)]
    [InlineData("d100", ModbusArea.HoldingRegister, 100)]
    [InlineData("M10", ModbusArea.Coil, 10)]
    [InlineData("Y3", ModbusArea.Coil, 3)]
    [InlineData("X5", ModbusArea.DiscreteInput, 5)]
    [InlineData("4x100", ModbusArea.HoldingRegister, 100)]
    [InlineData("0x10", ModbusArea.Coil, 10)]
    [InlineData("1x5", ModbusArea.DiscreteInput, 5)]
    [InlineData(" D200 ", ModbusArea.HoldingRegister, 200)]
    public void 合法地址_解析出正确的区域与偏移(string text, ModbusArea area, ushort offset)
    {
        var ok = ModbusAddressParser.TryParse(text, out var addr, out var err);

        Assert.True(ok, err);
        Assert.Equal(area, addr.Area);
        Assert.Equal(offset, addr.Offset);
        Assert.Null(err);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("D")]
    [InlineData("Dabc")]
    [InlineData("70000")]
    [InlineData("Z100")]
    [InlineData("3x100")]
    public void 非法地址_给出人话错误(string? text)
    {
        var ok = ModbusAddressParser.TryParse(text, out _, out var err);

        Assert.False(ok);
        Assert.NotNull(err);
        Assert.NotEmpty(err!);
    }

    [Fact]
    public void Parse_失败时抛异常_错误信息说人话()
    {
        var ex = Assert.Throws<FormatException>(() => ModbusAddressParser.Parse("Z100"));
        Assert.Contains("地址前缀", ex.Message);
    }
}

public class ModbusTcpPlcTests
{
    /// <summary>起一个临时端口上的进程内 Modbus 服务器，用完即弃。</summary>
    private static (ModbusTcpServer Server, int Port) StartServer()
    {
        // 先借 TcpListener 占一个空闲端口再还回去，避免写死端口撞车
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        var server = new ModbusTcpServer();
        server.ServerStart(port);   // 返回 void；后续连不上会在用例里以 Connect 失败暴露
        return (server, port);
    }

    [Fact]
    public void 连接成功_读写保持寄存器往返一致()
    {
        var (server, port) = StartServer();
        try
        {
            using var plc = new ModbusTcpPlc("127.0.0.1", port);
            Assert.True(plc.Connect(), plc.LastError);
            Assert.True(plc.IsConnected);

            var d100 = ModbusAddressParser.Parse("D100");
            Assert.True(plc.WriteShort(d100, 42));
            Assert.Equal((short)42, plc.ReadShort(d100));

            Assert.Null(plc.LastError);
        }
        finally
        {
            server.ServerClose();
            server.Dispose();
        }
    }

    [Fact]
    public void 读写线圈_往返一致()
    {
        var (server, port) = StartServer();
        try
        {
            using var plc = new ModbusTcpPlc("127.0.0.1", port);
            Assert.True(plc.Connect(), plc.LastError);

            var m10 = ModbusAddressParser.Parse("M10");
            Assert.True(plc.WriteBit(m10, true));
            Assert.True(plc.ReadBit(m10));
            Assert.True(plc.WriteBit(m10, false));
            Assert.False(plc.ReadBit(m10));
        }
        finally
        {
            server.ServerClose();
            server.Dispose();
        }
    }

    [Fact]
    public void 连不上_返回false并留下人话原因()
    {
        using var plc = new ModbusTcpPlc("127.0.0.1", 1);   // 端口1必有监听，正常环境连不上

        Assert.False(plc.Connect());
        Assert.False(plc.IsConnected);
        Assert.NotNull(plc.LastError);
        Assert.Contains("PLC 连接失败", plc.LastError);
    }

    [Fact]
    public void 区域用错_当场给出明确错误而不是莫名的通信失败()
    {
        var (server, port) = StartServer();
        try
        {
            using var plc = new ModbusTcpPlc("127.0.0.1", port);
            Assert.True(plc.Connect(), plc.LastError);

            var m10 = ModbusAddressParser.Parse("M10");
            Assert.Null(plc.ReadShort(m10));          // 16位读打在线圈上 → 拒绝
            Assert.Contains("不是保持寄存器", plc.LastError);

            var d100 = ModbusAddressParser.Parse("D100");
            Assert.False(plc.WriteBit(d100, true));   // 位写打在寄存器上 → 拒绝
            Assert.Contains("不是线圈", plc.LastError);
        }
        finally
        {
            server.ServerClose();
            server.Dispose();
        }
    }
}
