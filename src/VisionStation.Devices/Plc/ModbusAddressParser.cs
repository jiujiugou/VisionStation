namespace VisionStation.Devices.Plc;

/// <summary>
/// 把配置里的地址文本解析成 <see cref="PlcAddress"/>。
/// 逻辑复用自 NitroGateway 的 ModbusAddressParser（那边带 Domain 依赖，此处独立重写）。
///
/// <para>
/// <b>定位</b>：配置文本 → 点位的唯一翻译层。生产调用方只有两处：
/// <see cref="VisionStation.App.Composition.AppOptionsBinder"/> 做启动期校验（只 <c>TryParse</c> 看能不能过），
/// 以及组合根装配触发源/回写器时用 <see cref="Parse"/> 拿真点位（另有单测直接调它）。
/// 它是纯函数：不读 IO、不看配置、不抛（<see cref="Parse"/> 除外，见其注释），可以直接单测。
/// </para>
///
/// <para>
/// <b>为什么只认"前缀 + 数字"，不做一个宽松的通用解析</b>：地址写错在产线上的表现是
/// "读写一个不相干的寄存器，不报任何错" —— 计数对不上、心跳不动，但日志全是成功的。
/// 所以这里宁可多拒几种写法，也不做任何"猜"：不认识的前缀直接报错并列出支持项。
/// 启动期就把错误暴露出来，比轮询线程里静默哑掉便宜得多。
/// </para>
///
/// <para>
/// <b>当前明确不支持</b>（不是遗漏，是没做）：
/// <list type="bullet">
///   <item><b>位访问</b>（如 <c>D100.3</c>）—— 会整条解析失败。"只取第 3 位"这种需求若写成 <c>D100</c>
///         就会变成整字读写，把同寄存器里其它位一起改写，这类事故现场极难还原。</item>
///   <item><b>输入寄存器 3x</b> —— 见 <see cref="ModbusArea.InputRegister"/> 的说明。</item>
///   <item><b>浮点/32 位量</b> —— 本项目只读写单个 16 位，没有"两个寄存器拼一个 REAL"的概念，
///         自然也没有字序（ABCD/CDAB）配置。真要用必须显式配置字序，不能靠试 ——
///         字序猜错得到的是"看起来合理的错误数值"，比如 12.5 读成 0.0002。</item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// 支持两种写法：
/// 汇川/三菱习惯 —— D100（数据寄存器→保持寄存器4x）、M10（内部线圈→0x）、
///                  Y3（输出线圈→0x）、X5（物理输入→1x）；
/// Modbus 原生习惯 —— 4x100、0x10、1x5（3x 输入寄存器暂未用到，暂不开放）。
/// </remarks>
public static class ModbusAddressParser
{
    /// <summary>
    /// 解析地址。**不抛异常**：失败返回 false 并填 <paramref name="error"/>（可直接展示给现场）。
    /// </summary>
    /// <param name="text">配置原文，允许首尾空白</param>
    /// <param name="address">成功时的点位；失败为 default（调用方必须看返回值，不能只看 error 是否为 null）</param>
    /// <param name="error">失败原因，带原文与示例；成功时为 null</param>
    public static bool TryParse(string? text, out PlcAddress address, out string? error)
    {
        address = default;
        error = null;

        // 空值单独给一句带示例的话：配置文件里漏写一个键，最常见的结果就是空串，
        // 而"格式不合法"这种提示会让人去改一个根本没写的值。
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "地址为空（示例：D100 / M10 / 4x100）";
            return false;
        }

        var s = text.Trim();

        // Modbus 原生写法：区域前缀 + 偏移，如 4x100
        //
        // 要注意 "0x10" 在这里是"线圈 16"，不是十六进制 16 —— 这是 Modbus 的既有写法，
        // 与 C# 字面量撞脸。看到配置里写 0x 时先确认对方想表达哪一种。
        if (s.Length > 2 && (s[1] == 'x' || s[1] == 'X') && ushort.TryParse(s[2..], out var rawOffset))
        {
            var area = s[0] switch
            {
                '0' => ModbusArea.Coil,
                '1' => ModbusArea.DiscreteInput,
                '4' => ModbusArea.HoldingRegister,
                _ => default(ModbusArea?),
            };
            if (area is null)
            {
                error = $"不支持的寄存器区域 '{s[0]}x'（支持 0x线圈 / 1x离散输入 / 4x保持寄存器）: {text}";
                return false;
            }
            address = new PlcAddress(area.Value, rawOffset);
            return true;
        }

        // 汇川/三菱习惯：字母 + 偏移，如 D100
        //
        // 前缀大小写不敏感（d100 等价 D100）：现场手敲配置时大小写很难保证一致，
        // 而拒绝小写只会换来"抄了个地址却不能启动"，不换来任何安全。
        if (s.Length > 1 && ushort.TryParse(s[1..], out var offset))
        {
            var area = char.ToUpperInvariant(s[0]) switch
            {
                'D' => ModbusArea.HoldingRegister,
                'M' => ModbusArea.Coil,
                'Y' => ModbusArea.Coil,
                'X' => ModbusArea.DiscreteInput,
                _ => default(ModbusArea?),
            };
            if (area is null)
            {
                error = $"不认识的地址前缀 '{s[0]}'（汇川支持 D数据寄存器 / M中间线圈 / Y输出 / X输入，或 Modbus 原生 4x100）: {text}";
                return false;
            }
            address = new PlcAddress(area.Value, offset);
            return true;
        }

        // 走到这里的典型输入：D100.3（位访问）、4x（漏了偏移）、100（裸数字，无法判断区域）、
        // 70000（超出 ushort）。统一报一句"格式不合法 + 边界"，
        // 因为对现场来说这几种的处置方式完全一样：回头核对 PLC 变量表。
        error = $"地址格式不合法（示例：D100 / M10 / 4x100，偏移量必须是 0~65535）: {text}";
        return false;
    }

    /// <summary>
    /// 解析失败直接抛，用于启动期配置校验——配置错了就该当场炸，不能等到轮询时才哑掉。
    ///
    /// <para>
    /// 前置条件：调用方已经做过一次启动期校验（<c>AppOptionsBinder</c> 用 <see cref="TryParse"/> 逐项检查）。
    /// 因此正常情况下这里不会抛；它会抛，说明组合根绕过校验直接构造了驱动（测试或新入口），
    /// 那正是应该当场失败的情况。
    /// </para>
    /// </summary>
    public static PlcAddress Parse(string text) =>
        TryParse(text, out var addr, out var err) ? addr : throw new FormatException(err);
}
