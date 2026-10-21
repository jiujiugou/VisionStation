namespace VisionStation.Devices.Plc;

/// <summary>一个 Modbus 点位：功能区 + 偏移量。</summary>
/// <remarks>
/// 复用自 NitroGateway 的 ModbusAddress 思路，砍掉了 PointAddress 继承树——
/// 视觉站只需要"一个寄存器在哪"，不需要采集网关那套点位体系。
/// </remarks>
public readonly record struct PlcAddress(ModbusArea Area, ushort Offset)
{
    public override string ToString() => $"{Area}[{Offset}]";
}
