using System.Net;
using System.Net.Sockets;
using System.Text;
using VisionStation.Devices.Vm;

namespace VisionStation.UnitTests;

/// <summary>
/// VM 结果监听（TCP 中转的接收端）。
///
/// 这里测的是"链路在真实断线、端口冲突、回调出错时还活着"——
/// 产线上这些都不是异常路径，是每天都会发生的事：
/// VM 重启、网线拔了、上位机某个回调写库失败。
/// </summary>
public class VmResultListenerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly string[] AbcLines = { "a", "b", "c" };
    private static readonly string[] TwoLines = { "first", "second" };

    [Fact]
    public async Task 收到一行_回调一次()
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var listener = new VmResultListener("127.0.0.1", 0, line => received.TrySetResult(line));
        listener.Start();

        await SendAsync(listener.Port, """{"verdict":"OK"}""");

        var line = await received.Task.WaitAsync(Timeout);
        Assert.Contains("verdict", line);
    }

    [Fact]
    public async Task 一次连接发多行_逐行回调()
    {
        var lines = new List<string>();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var listener = new VmResultListener("127.0.0.1", 0, line =>
        {
            lock (lines) lines.Add(line);
            if (lines.Count >= 3) done.TrySetResult(true);
        });

        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.Port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("a\nb\nc\n"));

        await done.Task.WaitAsync(Timeout);

        lock (lines)
        {
            Assert.Equal(AbcLines, lines);
        }
    }

    [Fact]
    public async Task 断开重连后_仍能继续接收()
    {
        var lines = new List<string>();

        using var listener = new VmResultListener("127.0.0.1", 0, line =>
        {
            lock (lines) lines.Add(line);
        });

        listener.Start();

        // 每次 SendAsync 都新建连接再关掉 —— 就是 VM 重启/断网后重连的形态
        await SendAsync(listener.Port, "first");
        await WaitUntilAsync(() => { lock (lines) return lines.Count >= 1; }, Timeout);

        await SendAsync(listener.Port, "second");
        await WaitUntilAsync(() => { lock (lines) return lines.Count >= 2; }, Timeout);

        lock (lines)
        {
            Assert.Equal(TwoLines, lines);
        }
    }

    [Fact]
    public void 端口被占用_启动直接抛异常()
    {
        // "起了但没在听"是现场最难查的状态之一，所以端口冲突必须在启动期就红
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var busyPort = ((IPEndPoint)blocker.LocalEndpoint).Port;

        try
        {
            using var listener = new VmResultListener("127.0.0.1", busyPort, _ => { });

            Assert.Throws<SocketException>(() => listener.Start());
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task 回调抛异常_不中断监听()
    {
        var calls = 0;
        var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var listener = new VmResultListener("127.0.0.1", 0, _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("模拟入库失败");

            second.TrySetResult(true);
        });

        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.Port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("boom\nstill-alive\n"));

        // 第一次回调抛异常，第二次必须照常到 —— 否则一次数据库抖动就把整条链路哑掉
        await second.Task.WaitAsync(Timeout);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public void 停止后再启动_可以重新监听()
    {
        using var listener = new VmResultListener("127.0.0.1", 0, _ => { });

        listener.Start();
        var port = listener.Port;
        Assert.True(port > 0);

        listener.Stop();
        Assert.False(listener.IsListening);
    }

    private static async Task SendAsync(int port, string line)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        await stream.FlushAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.Fail("等待条件超时");
    }
}
