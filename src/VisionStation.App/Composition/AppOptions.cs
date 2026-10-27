using System.IO;
using Microsoft.Extensions.Configuration;
using VisionStation.Devices.Plc;
using VisionStation.Service.Housekeeping;

namespace VisionStation.App.Composition;

// ── 各段强类型选项 ──────────────────────────────────────────────────────────
// 全部是纯数据 record：解析与校验集中在 AppOptionsBinder，消费方只读不猜。
// 键名与 appsettings.json 的段名一一对应；默认值也只在这里出现一次。

/// <summary>
/// 定时触发间隔（毫秒）。只在"自动循环"这条轴上生效。
/// <para>
/// VM 模式下它基本没用：检测由平台推送驱动，本程序不采图。留着是因为
/// <c>InspectionPageViewModel</c> 的自动循环仍按这个节拍转，用来做心跳与超时判断。
/// </para>
/// </summary>
public sealed record TriggerOptions(int TimerIntervalMs);

/// <summary>PLC 传输方式。串口（RTU）是现场主流，TCP 保留给走网关/交换机的场合。</summary>
public enum PlcTransport
{
    Tcp = 0,
    Rtu = 1,
}

/// <summary>
/// PLC 通讯段。Enabled=false 时整段不生效（默认）——没有 PLC 的环境照常跑手动/定时触发。
/// 地址写法见 ModbusAddressParser：汇川习惯 D100/M10 或 Modbus 原生 4x100。
/// 寄存器分配（与 PLC 侧约定，AutoShop 变量监控表可验证）：
///   Trigger   = PLC 写 1 请求拍照，软件触发后回写 0（自清除）；
///   Result    = 软件每帧判定后写 1(OK)/2(NG)；
///   Heartbeat = 软件每次轮询 +1，PLC 看到它变化就知道软件活着。
///
/// 传输方式（Transport）：Tcp 用 Ip:Port；Rtu 用 SerialPort/BaudRate/DataBits/Parity/StopBits。
/// 两套参数**同时保留**，切换传输不用重新填另一套——Ip/Port 在 Rtu 下不参与，反之亦然。
/// 串口默认值是 H5U 出厂默认（9600-8-E-1），不是通用真理：现场以 PLC 侧实际配置为准，
/// 对不上就是一笔都读不通，且报错只显示超时。
/// </summary>
public sealed record PlcOptions(bool Enabled, string Ip, int Port, byte Station,
    string TriggerAddress, string ResultAddress, string HeartbeatAddress, int PollIntervalMs,
    PlcTransport Transport = PlcTransport.Tcp,
    string SerialPort = "COM3",
    int BaudRate = 9600,
    int DataBits = 8,
    SerialParity Parity = SerialParity.None,
    SerialStopBits StopBits = SerialStopBits.One,
    int SerialTimeoutMs = 500);

/// <summary>
/// 存储段。
/// <para>
/// 2026-10-27 删掉了原来的 <c>Mode</c>（All / NGOnly / Sampling）：
/// 它只被 <c>ImageStoreService</c> 读取，而那个服务随相机层一起删了
/// —— 本程序不再写图，图由 VisionMaster 存盘，我们只按报文路径去读。
/// 留着一个没人读的"存图策略"配置，比没有更容易误导人以为它还在生效。
/// </para>
/// </summary>
/// <param name="ImageFolder">
/// 图片目录（相对数据根）。**默认不配置 = 后台管家不管图片**。
///
/// <para>
/// 为什么默认关掉（2026-10-27，F-31）：图的生命周期已经整个归 VisionMaster ——
/// 它自己存、自己按「输出图像」模块里的数量/容量上限删。上位机再按目录扫一遍，
/// 等于两个进程各按自己的时钟删同一个目录，谁也不知道对方删了什么。
/// 而且自检会往这个目录写 <c>.write_test</c> 探针，指过去就是往别人的目录里写文件。
/// </para>
/// <para>
/// 保留这个键是留一个口子：<b>只有当明确要让上位机代管某个图片目录时</b>才填，
/// 填了就是"指哪儿清哪儿"，不做任何猜测。生产部署（图归 VM）应当保持为空。
/// </para>
/// </param>
/// <param name="DbPath">SQLite 文件（相对数据根）</param>
public sealed record StorageOptions(string? ImageFolder, string DbPath);

/// <summary>配方文件路径（相对数据根）。这是"兜底候选"，实际优先读 <c>.active</c> 指针指向的那份。</summary>
public sealed record RecipeOptions(string Path);

/// <summary>日志目录（相对数据根）。Serilog 按天滚动，保留 30 份。</summary>
public sealed record LoggingOptions(string LogFolder);

/// <summary>
/// VM 结果链路的监听参数。
/// </summary>
/// <param name="Enabled">是否启动监听。配方引擎不是 Vm 时保持 false，避免白起一个端口。</param>
/// <param name="Port">监听端口，VisionMaster 里「发送数据」模块配成 TCP 客户端连这里</param>
/// <param name="BindAddress">绑定地址。默认只监听本机，VM 与上位机同机部署时够用</param>
/// <param name="StallTimeoutSeconds">
/// 多久没收到结果算断流。**这是业务参数，没有通用值** —— 它由产线节拍和单件处理时间决定，
/// 只能在现场定（经验取节拍的 3~5 倍）。所以放配置，代码里不写死。
/// </param>
public sealed record VmLinkOptions(bool Enabled, int Port, string BindAddress, int StallTimeoutSeconds);

/// <summary>
/// 全部应用选项的聚合。绑定入口是 <see cref="AppOptionsBinder.Bind"/>。
/// </summary>
public sealed record AppOptions
{
    /// <summary>自动循环节拍。默认 2 秒：比人工看屏的反应还慢一档，够用且不吃 CPU。</summary>
    public TriggerOptions Trigger { get; init; } = new(2000);

    /// <summary>PLC 段。默认全部关闭 —— 没有 PLC 的机器不该因为缺配置起不来。</summary>
    public PlcOptions Plc { get; init; } = new(false, "127.0.0.1", 502, 1, "D100", "D101", "D102", 100);

    /// <summary>
    /// 存储段。<see cref="StorageOptions.ImageFolder"/> 为 null = 后台管家不管图片
    /// （图的生命周期归 VisionMaster，见该字段说明）。
    /// </summary>
    public StorageOptions Storage { get; init; } = new(null, "visionstation.db");

    /// <summary>兜底配方路径。</summary>
    public RecipeOptions Recipe { get; init; } = new(@"Config\recipes\default.json");

    /// <summary>日志目录。</summary>
    public LoggingOptions Logging { get; init; } = new("Logs");

    /// <summary>
    /// VM 结果链路的监听参数（TCP 中转模式）。
    /// 端口属于"这台机器"，不属于"这个产品"，所以放 appsettings 而不是配方 ——
    /// 换产品只换配方，改端口才动配置。
    /// </summary>
    public VmLinkOptions VmLink { get; init; } = new(false, 9100, "127.0.0.1", 10);

    /// <summary>Service 层的管家阈值 record，自带构造校验（阈值填错启动即失败），这里直接复用。</summary>
    public HousekeepingOptions Housekeeping { get; init; } = HousekeepingOptions.Default;

    /// <summary>
    /// 可写数据根（db / 日志 / 检测图片都落在它下面）。
    /// 旧数据不迁移（W-15 拍板：全部删除，从空库开始）—— 改造前的数据都是开发走查产物。
    /// 便携模式（W-15 拍板）：默认 = exe 所在目录（整目录自包含，拷走即搬迁），
    /// 可用启动参数 <c>--data-root=路径</c> 显式改到别处。**绝不使用 Environment.CurrentDirectory** ——
    /// 快捷方式/计划任务启动时 cwd 不可控，基于它落数据就是丢数据。
    /// </summary>
    public string DataRoot { get; init; } = AppContext.BaseDirectory;
}

/// <summary>
/// 配置绑定与校验的唯一入口（W-14 收口：此前同一键被两处各读一次、
/// 各处直接 <c>config["..."]</c> 且缺项静默回落）。规则：
///
/// - 数字解析失败 → 报错**带键名**，不是裸 FormatException；
/// - 枚举值（相机/存储模式）不在合法集合 → 报错并列出合法值；
/// - 任何错误聚合成一个 <see cref="OptionsValidationException"/>，启动期非 0 退出；
/// - **不静默回落**：缺项才用默认值，值非法就是错误。
/// </summary>
public static class AppOptionsBinder
{
    /// <summary>
    /// 读配置 → 校验 → 产出不可变的 <see cref="AppOptions"/>。
    ///
    /// <para>
    /// 流程刻意做成"先收集全部错误、最后一次性抛出"：现场改配置常常一次改好几项，
    /// 报一个错、改一处、再启动、再报下一个，来回开机是纯浪费。
    /// </para>
    /// <para>
    /// 抛 <see cref="AppOptionsValidationException"/> 而不是返回 null：
    /// 调用方（<c>AppHost.Configure</c>）没有任何合理的降级动作，
    /// 让他被迫写 if 判断只会掩盖问题。
    /// </para>
    /// </summary>
    /// <param name="config">已加载的配置（appsettings.json）</param>
    /// <param name="dataRootOverride">启动参数 <c>--data-root=</c> 的值；空 = 用 exe 目录</param>
    public static AppOptions Bind(IConfiguration config, string? dataRootOverride = null)
    {
        var errors = new List<string>();

        var trigger = new TriggerOptions(
            TimerIntervalMs: ReadInt(config, "Trigger:TimerIntervalMs", 2000, errors));
        if (trigger.TimerIntervalMs <= 0)
            errors.Add("Trigger:TimerIntervalMs 必须大于 0（毫秒）");

        var plc = ReadPlc(config, errors);

        var storage = new StorageOptions(
            // 空/缺省 = null = 后台管家不管图片（默认如此，图由 VM 存、VM 删）。
            // 这里刻意不给默认目录：给一个默认值，清理就会去扫一个永远空着的目录，
            // 每小时打一条"清理完成 0MB"，看起来在工作、实际什么也没管。
            ImageFolder: ReadOptional(config, "Storage:ImageFolder"),
            DbPath: Read(config, "Storage:DbPath", "visionstation.db"));

        var recipe = new RecipeOptions(
            Read(config, "Recipe:Path", @"Config\recipes\default.json"));

        var logging = new LoggingOptions(
            Read(config, "Logging:LogFolder", "Logs"));

        var housekeeping = ReadHousekeeping(config, errors);

        var vmLink = ReadVmLink(config, errors);

        var dataRoot = ResolveDataRoot(dataRootOverride);

        if (errors.Count > 0)
            throw new AppOptionsValidationException(errors);

        return new AppOptions
        {
            Trigger = trigger,
            Plc = plc,
            Storage = storage,
            Recipe = recipe,
            Logging = logging,
            Housekeeping = housekeeping,
            VmLink = vmLink,
            DataRoot = dataRoot
        };
    }

    /// <summary>
    /// VM 链路：Enabled=false 时其余键全部走默认值不校验（没有 VM 的环境不该被迫配它）；
    /// Enabled=true 时端口必须合法 —— 端口配错会让现场一直等不到结果，比启动期报错难查得多。
    /// </summary>
    private static VmLinkOptions ReadVmLink(IConfiguration config, List<string> errors)
    {
        var enabled = ReadBool(config, "VmLink:Enabled", false, errors);
        if (!enabled)
            return new VmLinkOptions(false, 9100, "127.0.0.1", 10);

        var port = ReadInt(config, "VmLink:Port", 9100, errors);
        if (port is <= 0 or > 65535)
            errors.Add("VmLink:Port 必须在 1~65535 之间");

        // 断流阈值同样要在启动期校验：填 0 会让链路每秒报一次断流，填太大等于没有告警
        var stallSeconds = ReadInt(config, "VmLink:StallTimeoutSeconds", 10, errors);
        if (stallSeconds is < 1 or > 3600)
            errors.Add("VmLink:StallTimeoutSeconds 必须在 1~3600 秒之间");

        return new VmLinkOptions(true, port, Read(config, "VmLink:BindAddress", "127.0.0.1"), stallSeconds);
    }

    /// <summary>
    /// PLC 段：Enabled=false 时其余键全部走默认值不校验（没有 PLC 的环境不该被迫配它）；
    /// Enabled=true 时端口/轮询间隔/三个地址全部校验，地址非法启动期就报——
    /// 配置错当场炸，不能等到轮询线程里静默哑掉。
    /// </summary>
    private static PlcOptions ReadPlc(IConfiguration config, List<string> errors)
    {
        var enabled = ReadBool(config, "Plc:Enabled", false, errors);
        if (!enabled)
            return new PlcOptions(false, "127.0.0.1", 502, 1, "D100", "D101", "D102", 100);

        var ip = Read(config, "Plc:Ip", "127.0.0.1");
        var port = ReadInt(config, "Plc:Port", 502, errors);
        if (port is <= 0 or > 65535)
            errors.Add("Plc:Port 必须在 1~65535 之间");

        var poll = ReadInt(config, "Plc:PollIntervalMs", 100, errors);
        if (poll <= 0)
            errors.Add("Plc:PollIntervalMs 必须大于 0（毫秒）");

        var station = ReadByte(config, "Plc:Station", 1, errors);

        var triggerAddr = Read(config, "Plc:TriggerAddress", "D100");
        if (!ModbusAddressParser.TryParse(triggerAddr, out _, out var e1))
            errors.Add($"Plc:TriggerAddress {e1}");
        var resultAddr = Read(config, "Plc:ResultAddress", "D101");
        if (!ModbusAddressParser.TryParse(resultAddr, out _, out var e2))
            errors.Add($"Plc:ResultAddress {e2}");
        var heartbeatAddr = Read(config, "Plc:HeartbeatAddress", "D102");
        if (!ModbusAddressParser.TryParse(heartbeatAddr, out _, out var e3))
            errors.Add($"Plc:HeartbeatAddress {e3}");

        // 传输方式：默认 Tcp（保持既有配置的语义不变）；现场主流是 Rtu
        var transport = ReadEnum(config, "Plc:Transport", PlcTransport.Tcp, errors);
        var serialPort = Read(config, "Plc:SerialPort", "COM3");
        var baud = ReadInt(config, "Plc:BaudRate", 9600, errors);
        var dataBits = ReadInt(config, "Plc:DataBits", 8, errors);
        var serialTimeout = ReadInt(config, "Plc:SerialTimeoutMs", 500, errors);
        var parity = ReadEnum(config, "Plc:Parity", SerialParity.None, errors);
        var stopBits = ReadEnum(config, "Plc:StopBits", SerialStopBits.One, errors);

        // 串口参数只在 Rtu 下校验：用 Tcp 的环境不该被一串串口默认值卡住启动
        if (transport == PlcTransport.Rtu)
        {
            if (string.IsNullOrWhiteSpace(serialPort))
                errors.Add("Plc:SerialPort 在 Rtu 模式下不能为空（例如 COM3）");
            if (baud <= 0)
                errors.Add("Plc:BaudRate 必须大于 0（现场常见 9600 / 19200 / 38400 / 115200）");
            if (dataBits is < 5 or > 8)
                errors.Add("Plc:DataBits 必须在 5~8 之间（Modbus RTU 实际只支持 8）");
            if (serialTimeout is < 50 or > 5000)
                errors.Add("Plc:SerialTimeoutMs 必须在 50~5000 毫秒之间（串口卡住时它决定回写排队多长）");
        }

        return new PlcOptions(true, ip, port, station, triggerAddr, resultAddr, heartbeatAddr, poll,
            transport, serialPort, baud, dataBits, parity, stopBits, serialTimeout);
    }

    /// <summary>
    /// 枚举型配置项。**只接受名字不接受数字**——"Plc:Transport": "1" 看着像端口号，
    /// 静默当成 Rtu 是现场最难查的一类错，所以数字直接报错。
    /// </summary>
    private static T ReadEnum<T>(IConfiguration config, string key, T defaultValue, List<string> errors)
        where T : struct, Enum
    {
        var text = config[key];
        if (string.IsNullOrWhiteSpace(text)) return defaultValue;
        if (text.All(char.IsAsciiDigit))
        {
            errors.Add($"{key} = \"{text}\" 不接受数字，请填名字：{string.Join(" / ", Enum.GetNames<T>())}");
            return defaultValue;
        }
        if (Enum.TryParse<T>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            return parsed;
        errors.Add($"{key} = \"{text}\" 不合法，可选：{string.Join(" / ", Enum.GetNames<T>())}");
        return defaultValue;
    }

    /// <summary>读 0~255 的字节值（Modbus 从站地址）。超范围由 <c>TryParse</c> 自己失败，这里不额外判断。</summary>
    private static byte ReadByte(IConfiguration config, string key, byte defaultValue, List<string> errors)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (byte.TryParse(value, out var parsed)) return parsed;
        errors.Add($"{key} = \"{value}\" 不是合法字节（0~255）");
        return defaultValue;
    }

    /// <summary>
    /// 管家阈值段。默认值取自 <see cref="HousekeepingOptions.Default"/>，
    /// 而不是在这里再抄一份数字 —— 抄两份的下场是有一份忘了改。
    /// </summary>
    /// <remarks>
    /// <c>MaxImageFolderMB</c> 在配置里是"兆字节"（人能填），在 record 里是"字节"（代码好用）：
    /// 读进来乘 1024*1024，存下去除回去。这个换算是这里唯一需要小心的地方。
    /// </remarks>
    private static HousekeepingOptions ReadHousekeeping(IConfiguration config, List<string> errors)
    {
        var d = HousekeepingOptions.Default;
        // Service 层的 record 自带构造校验；这里只负责把键值安全地读成数字（带键名报错）。
        return new HousekeepingOptions(
            ReadInt(config, "Housekeeping:MaxMemoryMB", d.MaxMemoryMb, errors),
            ReadInt(config, "Housekeeping:KeepRecordsDays", d.KeepRecordsDays, errors),
            ReadInt(config, "Housekeeping:ImageRetentionDays", d.ImageRetentionDays, errors),
            ReadLong(config, "Housekeeping:MaxImageFolderMB", d.MaxImageFolderBytes / 1024 / 1024, errors) * 1024 * 1024,
            ReadInt(config, "Housekeeping:MemoryCheckIntervalMs", d.MemoryCheckIntervalMs, errors),
            ReadInt(config, "Housekeeping:DatabaseCleanIntervalMs", d.DatabaseCleanIntervalMs, errors));
    }

    /// <summary>
    /// 数据根解析：显式参数 &gt; exe 目录。相对路径按 exe 目录解析（显式但写短了也成立）。
    /// 这里转成绝对路径，后面所有落盘计算都不再依赖进程状态。
    /// </summary>
    private static string ResolveDataRoot(string? dataRootOverride)
    {
        if (string.IsNullOrWhiteSpace(dataRootOverride))
            return AppContext.BaseDirectory;

        try
        {
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, dataRootOverride));
        }
        catch (Exception ex)
        {
            // 用户显式给错了参数，报错退出比悄悄回落更诚实
            throw new AppOptionsValidationException(
                new[] { $"--data-root = \"{dataRootOverride}\" 无法解析为路径：{ex.Message}" });
        }
    }

    /// <summary>
    /// 字符串项。只在"缺项或全空白"时用默认值 —— 也就是说，配了空字符串等于没配。
    /// 顺带 Trim：现场手工编辑 appsettings 时极易带上首尾空格（复制粘贴 IP 尤其常见），
    /// 而带空格的 "127.0.0.1 " 会让 Socket 连接直接失败且报错信息看不出问题。
    /// </summary>
    private static string Read(IConfiguration config, string key, string defaultValue)
    {
        var value = config[key];
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    /// <summary>
    /// 可选项：缺省或空白 → <c>null</c>，**不填默认值**。
    ///
    /// 与 <see cref="Read"/> 的区别是语义：<c>Read</c> 回答"没配时用什么"，
    /// 本方法回答"用户到底配了没有"。有些项的"没配"本身就是一个有意义的取值 ——
    /// 例如 <c>Storage:ImageFolder</c> 没配 = 后台管家不管图片（图归 VM 存删）。
    /// 用 <c>Read</c> 给它填一个默认目录，会把"没启用"伪装成"已启用但目录是空的"。
    /// </summary>
    private static string? ReadOptional(IConfiguration config, string key)
    {
        var value = config[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>布尔项。只认 true/false（不认 1/0），写错了当配置错误报出来。</summary>
    private static bool ReadBool(IConfiguration config, string key, bool defaultValue, List<string> errors)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (bool.TryParse(value, out var parsed)) return parsed;
        errors.Add($"{key} = \"{value}\" 不是合法布尔值（true/false）");
        return defaultValue;
    }

    /// <summary>整数项。解析失败记一条带键名的错误，并返回默认值让后续校验继续跑（一次报全）。</summary>
    private static int ReadInt(IConfiguration config, string key, int defaultValue, List<string> errors)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (int.TryParse(value, out var parsed)) return parsed;
        errors.Add($"{key} = \"{value}\" 不是合法整数");
        return defaultValue;
    }

    /// <summary>长整数项。只有图片目录上限（字节数）需要它 —— int 装不下几 GB。</summary>
    private static long ReadLong(IConfiguration config, string key, long defaultValue, List<string> errors)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (long.TryParse(value, out var parsed)) return parsed;
        errors.Add($"{key} = \"{value}\" 不是合法整数");
        return defaultValue;
    }
}

/// <summary>
/// 配置校验失败。不用框架的 <c>OptionsValidationException</c>：它的构造要求 optionsType，
/// 而这里的失败属于"整份文件"，聚合错误列表比绑到某个类型更符合实际。
/// </summary>
public sealed class AppOptionsValidationException : Exception
{
    /// <summary>
    /// 全部校验错误（不是第一条）。启动日志与 crash.log 会把它们逐条打印，
    /// 现场一次就能改完。
    /// </summary>
    public IReadOnlyList<string> Errors { get; }

    public AppOptionsValidationException(IReadOnlyList<string> errors)
        : base("appsettings.json 校验失败（不静默回落默认值，修正后重试）：\n  - "
               + string.Join("\n  - ", errors))
    {
        Errors = errors;
    }
}
