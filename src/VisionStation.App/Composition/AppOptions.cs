using System.IO;
using Microsoft.Extensions.Configuration;
using VisionStation.Devices.Plc;
using VisionStation.Service.Housekeeping;

namespace VisionStation.App.Composition;

// ── 各段强类型选项 ──────────────────────────────────────────────────────────
// 全部是纯数据 record：解析与校验集中在 AppOptionsBinder，消费方只读不猜。
// 键名与 appsettings.json 的段名一一对应；默认值也只在这里出现一次。

public sealed record CameraOptions(string Mode, string ImageFolder, string FileFilter, bool Loop);

public sealed record TriggerOptions(int TimerIntervalMs);

/// <summary>
/// PLC 通讯段。Enabled=false 时整段不生效（默认）——没有 PLC 的环境照常跑手动/定时触发。
/// 地址写法见 ModbusAddressParser：汇川习惯 D100/M10 或 Modbus 原生 4x100。
/// 寄存器分配（与 PLC 侧约定，AutoShop 变量监控表可验证）：
///   Trigger   = PLC 写 1 请求拍照，软件触发后回写 0（自清除）；
///   Result    = 软件每帧判定后写 1(OK)/2(NG)；
///   Heartbeat = 软件每次轮询 +1，PLC 看到它变化就知道软件活着。
/// </summary>
public sealed record PlcOptions(bool Enabled, string Ip, int Port, byte Station,
    string TriggerAddress, string ResultAddress, string HeartbeatAddress, int PollIntervalMs);

public sealed record StorageOptions(string Mode, string ImageFolder, string DbPath);

public sealed record RecipeOptions(string Path);

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
    public CameraOptions Camera { get; init; } = new("File", "TestImages", "*.jpg;*.png;*.bmp", true);
    public TriggerOptions Trigger { get; init; } = new(2000);
    public PlcOptions Plc { get; init; } = new(false, "127.0.0.1", 502, 1, "D100", "D101", "D102", 100);
    public StorageOptions Storage { get; init; } = new("All", "InspectedImages", "visionstation.db");
    public RecipeOptions Recipe { get; init; } = new(@"Config\recipes\default.json");
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
/// 配置绑定与校验的唯一入口（W-14 收口：此前 Camera:Mode 被读两次、
/// 各处直接 <c>config["..."]</c> 且缺项静默回落）。规则：
///
/// - 数字解析失败 → 报错**带键名**，不是裸 FormatException；
/// - 枚举值（相机/存储模式）不在合法集合 → 报错并列出合法值；
/// - 任何错误聚合成一个 <see cref="OptionsValidationException"/>，启动期非 0 退出；
/// - **不静默回落**：缺项才用默认值，值非法就是错误。
/// </summary>
public static class AppOptionsBinder
{
    public static AppOptions Bind(IConfiguration config, string? dataRootOverride = null)
    {
        var errors = new List<string>();

        var camera = new CameraOptions(
            Mode: Read(config, "Camera:Mode", "File"),
            ImageFolder: Read(config, "Camera:ImageFolder", "TestImages"),
            FileFilter: Read(config, "Camera:FileFilter", "*.jpg;*.png;*.bmp"),
            Loop: ReadBool(config, "Camera:Loop", true, errors));

        var allowedCameraModes = new[] { "Webcam", "Hikvision", "File" };
        if (!allowedCameraModes.Contains(camera.Mode, StringComparer.OrdinalIgnoreCase))
            errors.Add($"Camera:Mode = \"{camera.Mode}\" 不合法，可选值：{string.Join(", ", allowedCameraModes)}");

        var trigger = new TriggerOptions(
            TimerIntervalMs: ReadInt(config, "Trigger:TimerIntervalMs", 2000, errors));
        if (trigger.TimerIntervalMs <= 0)
            errors.Add("Trigger:TimerIntervalMs 必须大于 0（毫秒）");

        var plc = ReadPlc(config, errors);

        var storage = new StorageOptions(
            Mode: Read(config, "Storage:Mode", "All"),
            ImageFolder: Read(config, "Storage:ImageFolder", "InspectedImages"),
            DbPath: Read(config, "Storage:DbPath", "visionstation.db"));

        var allowedStorageModes = new[] { "All", "NGOnly", "Sampling" };
        if (!allowedStorageModes.Contains(storage.Mode, StringComparer.OrdinalIgnoreCase))
            errors.Add($"Storage:Mode = \"{storage.Mode}\" 不合法，可选值：{string.Join(", ", allowedStorageModes)}");

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
            Camera = camera,
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

        return new PlcOptions(true, ip, port, station, triggerAddr, resultAddr, heartbeatAddr, poll);
    }

    private static byte ReadByte(IConfiguration config, string key, byte defaultValue, List<string> errors)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (byte.TryParse(value, out var parsed)) return parsed;
        errors.Add($"{key} = \"{value}\" 不是合法字节（0~255）");
        return defaultValue;
    }

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

    private static string Read(IConfiguration config, string key, string defaultValue)
    {
        var value = config[key];
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private static bool ReadBool(IConfiguration config, string key, bool defaultValue, List<string> errors)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (bool.TryParse(value, out var parsed)) return parsed;
        errors.Add($"{key} = \"{value}\" 不是合法布尔值（true/false）");
        return defaultValue;
    }

    private static int ReadInt(IConfiguration config, string key, int defaultValue, List<string> errors)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (int.TryParse(value, out var parsed)) return parsed;
        errors.Add($"{key} = \"{value}\" 不是合法整数");
        return defaultValue;
    }

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
    public IReadOnlyList<string> Errors { get; }

    public AppOptionsValidationException(IReadOnlyList<string> errors)
        : base("appsettings.json 校验失败（不静默回落默认值，修正后重试）：\n  - "
               + string.Join("\n  - ", errors))
    {
        Errors = errors;
    }
}
