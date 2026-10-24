using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VisionStation.VmSimulator;

// VisionMaster 结果联调工具。
//
// 两种模式：
//   ① 默认（模拟）：往 VisionStation 的 VM 链路发结果，验证接收侧。
//   ② --listen（抓包）：自己当服务端，把 VisionMaster「发送数据」发过来的**原始字节**打印出来。
//      第一次接真实 VM 时必须先用它看一眼 —— VM 那边一个格式串写错，接收端就只会收到一堆
//      对不上的字符串，而界面上的表现只是"没反应"，没有这一步基本查不出问题在哪。
//
// 用法示例：
//   dotnet run --project tools/VmSimulator
//   dotnet run --project tools/VmSimulator -- --port 9100 --count 5 --interval 1000
//   dotnet run --project tools/VmSimulator -- --verdict NG --image "D:\img\1.bmp"
//   dotnet run --project tools/VmSimulator -- --raw '{"verdict":"OK"}'
//   dotnet run --project tools/VmSimulator -- --listen --port 9100
//
// 不带 --raw 时按参数拼 JSON；带 --raw 时原样发（用来试畸形数据，验证解析失败也会入库为 NG）。

// 控制台默认是 GBK 代码页，中文在 UTF-8 的终端/重定向里会变成乱码。
// 抓到的是别家的数据，看不清楚等于白抓，所以这里统一成 UTF-8。
try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // 没有可写控制台（被重定向到文件/服务）时设置会失败，不影响抓包本身
}

var options = new Options();

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host": options.Host = Next(args, ref i); break;
        case "--port": options.Port = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
        case "--count": options.Count = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
        case "--interval": options.IntervalMs = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
        case "--verdict": options.Verdict = Next(args, ref i); break;
        case "--image": options.ImagePath = Next(args, ref i); break;
        case "--raw": options.RawJson = Next(args, ref i); break;
        case "--listen": options.Listen = true; break;
        case "--timeout": options.ListenTimeoutSec = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
        case "--help":
        case "-h":
            PrintHelp();
            return 0;
        default:
            await Console.Error.WriteLineAsync($"未知参数：{args[i]}（用 --help 看用法）");
            return 1;
    }
}

if (options.Listen)
    return await ListenAsync(options);

await Console.Out.WriteLineAsync($"目标 {options.Host}:{options.Port}  帧数 {options.Count}  间隔 {options.IntervalMs}ms");

var sent = 0;
for (var n = 0; n < options.Count; n++)
{
    var line = options.RawJson ?? BuildJson(options, n);

    try
    {
        using var client = new TcpClient();
        await client.ConnectAsync(options.Host, options.Port);
        await using var stream = client.GetStream();

        // 一行一条，**必须带换行**：接收端按行定界
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        await stream.FlushAsync();

        sent++;
        await Console.Out.WriteLineAsync($"  [{n + 1}/{options.Count}] 已发送 {line.Length} 字符");
    }
    catch (SocketException ex)
    {
        await Console.Error.WriteLineAsync($"  连接失败（{options.Host}:{options.Port}）：{ex.Message}");
        await Console.Error.WriteLineAsync("  检查：VisionStation 是否在运行？appsettings 里 VmLink:Enabled 是否为 true？端口是否一致？");
        return 2;
    }

    if (n < options.Count - 1 && options.IntervalMs > 0)
        await Task.Delay(options.IntervalMs);
}

await Console.Out.WriteLineAsync($"完成，共发出 {sent} 帧。");
await Console.Out.WriteLineAsync("去 VisionStation 的检测页看结果；历史页/数据库里应该能查到这些记录。");
return 0;

static string Next(string[] argv, ref int i)
{
    if (i + 1 >= argv.Length)
        throw new ArgumentException($"参数 {argv[i]} 后面缺少值");
    return argv[++i];
}

/// <summary>
/// 抓包模式：把 VisionMaster 发过来的东西原样打出来。
///
/// 打印三样东西，缺一不可：
///   · 字节数 —— 判断它到底发了多少，是不是只发了一个空行；
///   · 可读文本 —— 直接看出字段顺序和分隔符；
///   · 转义形式 —— 把 \r \n \0 显出来。**这一项最容易救命**：
///     很多"收不到"其实是对方发了 \r\n 而两边对结束符的理解不一致，
///     或者发的是 GBK 而按 UTF-8 解出来是乱码。
/// </summary>
static async Task<int> ListenAsync(Options o)
{
    var listener = new TcpListener(IPAddress.Parse(o.Host), o.Port);

    try
    {
        listener.Start();
    }
    catch (SocketException ex)
    {
        await Console.Error.WriteLineAsync($"端口 {o.Port} 监听失败：{ex.Message}");
        await Console.Error.WriteLineAsync("  多半是 VisionStation 已经在用这个端口（VmLink:Enabled=true 时它会占住）。");
        await Console.Error.WriteLineAsync("  抓包期间请先把 VisionStation 关掉，或者改用 --port 指定别的端口。");
        return 2;
    }

    var local = (IPEndPoint)listener.LocalEndpoint;
    await Console.Out.WriteLineAsync($"抓包模式：监听 {local.Address}:{local.Port}");
    await Console.Out.WriteLineAsync("在 VisionMaster 里跑一次流程（末尾要有「发送数据」模块），这里会原样显示它发的内容。");
    if (o.ListenTimeoutSec > 0)
        await Console.Out.WriteLineAsync($"{o.ListenTimeoutSec} 秒后自动退出。");

    using var cts = new CancellationTokenSource(
        o.ListenTimeoutSec > 0 ? TimeSpan.FromSeconds(o.ListenTimeoutSec) : Timeout.InfiniteTimeSpan);

    var frames = 0;
    var chained = 0;

    try
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
            chained++;
            await Console.Out.WriteLineAsync();
            await Console.Out.WriteLineAsync($"=== 连接 #{chained}：{remote} ({DateTime.Now:HH:mm:ss.fff}) ===");

            // 每次连接都新读一段，直到对方关闭或超时。用 1 秒读超时判断"这一批发完了"。
            using (client)
            {
                client.ReceiveTimeout = 2000;
                var buffer = new byte[8192];
                var stream = client.GetStream();

                while (!cts.IsCancellationRequested)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(buffer, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (IOException ex)
                    {
                        await Console.Out.WriteLineAsync($"  读取中断：{ex.Message}");
                        break;
                    }

                    if (read <= 0)
                    {
                        await Console.Out.WriteLineAsync("  （对端已关闭连接）");
                        break;
                    }

                    frames++;
                    var chunk = buffer.AsSpan(0, read).ToArray();

                    await Console.Out.WriteLineAsync($"--- 第 {frames} 段，{read} 字节 ---");
                    await Console.Out.WriteLineAsync($"  文本：{Decode(chunk)}");
                    await Console.Out.WriteLineAsync($"  转义：{EscapeBytes(chunk)}");
                }
            }
        }
    }
    finally
    {
        listener.Stop();
    }

    await Console.Out.WriteLineAsync();
    await Console.Out.WriteLineAsync($"结束：共 {chained} 次连接、{frames} 段数据。");
    if (frames == 0)
    {
        await Console.Out.WriteLineAsync("一段都没收到，按这个顺序查：");
        await Console.Out.WriteLineAsync("  1. VM 里「系统 → 通信管理」有没有建 TCP 客户端设备，地址是不是 127.0.0.1:端口；");
        await Console.Out.WriteLineAsync("  2. 「发送数据」模块的「输出至」选的是不是「通信设备」（默认是「数据队列」，不发网络）；");
        await Console.Out.WriteLineAsync("  3. 模块在流程里有没有真的被执行（是否被条件模块跳过）；");
        await Console.Out.WriteLineAsync("  4. 流程跑没跑 —— 发送数据只在流程运行时才发。");
    }

    return frames > 0 ? 0 : 1;
}

/// <summary>先按 UTF-8 解；不合法就按 GB18030（VM 默认常是 GBK，中文会变乱码）。</summary>
static string Decode(byte[] data)
{
    try
    {
        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
    }
    catch (DecoderFallbackException)
    {
        try
        {
            return Encoding.GetEncoding("GB18030").GetString(data);
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1.GetString(data);
        }
    }
}

/// <summary>把不可见字符显式写出来：\r \n \t \0 与高位字节用 \xNN。</summary>
static string EscapeBytes(byte[] data)
{
    var sb = new StringBuilder(data.Length + 16);
    foreach (var b in data)
    {
        switch (b)
        {
            case (byte)'\r': sb.Append("\\r"); break;
            case (byte)'\n': sb.Append("\\n"); break;
            case (byte)'\t': sb.Append("\\t"); break;
            case 0: sb.Append("\\0"); break;
            case >= 0x20 and < 0x7F: sb.Append((char)b); break;
            default: sb.Append("\\x").Append(b.ToString("X2", CultureInfo.InvariantCulture)); break;
        }
    }
    return sb.ToString();
}

static string BuildJson(Options o, int index)
{
    var verdict = o.Verdict;
    var ok = verdict.Equals("OK", StringComparison.OrdinalIgnoreCase);

    var sb = new StringBuilder();
    sb.Append("{\"verdict\":\"").Append(verdict).Append('"');

    if (!string.IsNullOrWhiteSpace(o.ImagePath))
        sb.Append(",\"imagePath\":\"").Append(Escape(o.ImagePath)).Append('"');

    sb.Append(",\"triggerSource\":\"VM\"");
    sb.Append(",\"sn\":\"SIM-").Append((index + 1).ToString("D4", CultureInfo.InvariantCulture)).Append('"');

    sb.Append(",\"items\":[");
    sb.Append("{\"label\":\"外径\",\"value\":").Append(o.Diameter.ToString("0.###", CultureInfo.InvariantCulture))
      .Append(",\"unit\":\"mm\",\"ok\":").Append(ok ? "true" : "false").Append('}');
    sb.Append(",{\"label\":\"孔数\",\"value\":8,\"ok\":true}");
    sb.Append("]}");

    return sb.ToString();
}

static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal)
    .Replace("\"", "\\\"", StringComparison.Ordinal);

static void PrintHelp()
{
    Console.WriteLine("""
        VmSimulator —— 模拟 VisionMaster 往 VisionStation 推检测结果

        参数：
          --host <ip>        默认 127.0.0.1
          --port <port>      默认 9100（要和 appsettings 的 VmLink:Port 一致）
          --count <n>        发几帧，默认 1
          --interval <ms>    帧间隔，默认 1000
          --verdict <OK|NG>  总判定，默认 OK
          --image <path>     VM 侧存图的路径（可选，给了界面才有图看）
          --raw <json>       原样发送这一行（试畸形数据用）
          --listen           抓包模式：自己当服务端，把 VM 发过来的原始字节打出来
          --timeout <sec>    抓包模式多少秒后自动退出（默认一直等，Ctrl+C 结束）

        退出码：0 成功；1 参数错误 / 抓包没收到数据；2 连不上 / 端口被占。
        """);
}
