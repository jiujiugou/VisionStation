namespace VisionStation.Devices.Plc;

/// <summary>
/// 把配置里的地址文本解析成 <see cref="PlcAddress"/>。
/// 逻辑复用自 NitroGateway 的 ModbusAddressParser（那边带 Domain 依赖，此处独立重写）。
/// </summary>
/// <remarks>
/// 支持两种写法：
/// 汇川/三菱习惯 —— D100（数据寄存器→保持寄存器4x）、M10（内部线圈→0x）、
///                  Y3（输出线圈→0x）、X5（物理输入→1x）；
/// Modbus 原生习惯 —— 4x100、0x10、1x5（3x 输入寄存器暂未用到，暂不开放）。
/// </remarks>
public static class ModbusAddressParser
{
    public static bool TryParse(string? text, out PlcAddress address, out string? error)
    {
        address = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "地址为空（示例：D100 / M10 / 4x100）";
            return false;
        }

        var s = text.Trim();

        // Modbus 原生写法：区域前缀 + 偏移，如 4x100
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

        error = $"地址格式不合法（示例：D100 / M10 / 4x100，偏移量必须是 0~65535）: {text}";
        return false;
    }

    /// <summary>解析失败直接抛，用于启动期配置校验——配置错了就该当场炸，不能等到轮询时才哑掉。</summary>
    public static PlcAddress Parse(string text) =>
        TryParse(text, out var addr, out var err) ? addr : throw new FormatException(err);
}
