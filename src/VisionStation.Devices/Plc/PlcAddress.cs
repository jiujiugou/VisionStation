namespace VisionStation.Devices.Plc;

/// <summary>
/// 一个 Modbus 点位：功能区 + 偏移量。
///
/// <para>
/// <b>定位</b>：配置解析（<see cref="ModbusAddressParser"/>）的产物，也是
/// <see cref="IPlcConnection"/> 四个读写方法的入参 —— 上层拿到的永远是一个"解好的点位"，
/// 不会把地址字符串一路带到 IO 层再解析（那等于每 100ms 解析一次配置，且错误要等轮询才发现）。
/// </para>
/// <para>
/// <b>⚠ <see cref="Offset"/> 是零基偏移，不是手册上的 40001 式编号</b>：
/// <c>D100</c> → 4x 区 offset 100 → 线上报文里的地址字段就是 100。
/// 汇川/三菱的 D 编号本身就是零基，所以两者能直接对上。
/// </para>
/// <para>
/// 换到"手册从 40001 起编号"的 PLC（西门子那类）时，必须人工换算一次：
/// 直接把 40001 填进配置会被解析器拒绝（既不是 <c>4xNNN</c> 也不是字母+偏移，不会静默通过）；
/// 真正的风险是**换算时少减或多减**，写成 <c>4x1</c> 这种合法地址 ——
/// 那不会报错，只会稳定地读写隔壁那个寄存器，表现为"计数对不上但通信全成功"。
/// 所以换品牌时的点位核对不能省，这也是本项目只用汇川/三菱写法的原因。
/// </para>
/// <para>
/// 用 <c>readonly record struct</c> 而不是类：它只是两个字段的载体，按值比较与按值复制更符合预期
/// （两个 <c>new PlcAddress(HoldingRegister, 100)</c> 应当被视为同一个点位）。
/// </para>
/// </summary>
/// <remarks>
/// 复用自 NitroGateway 的 ModbusAddress 思路，砍掉了 PointAddress 继承树——
/// 视觉站只需要"一个寄存器在哪"，不需要采集网关那套点位体系。
/// </remarks>
public readonly record struct PlcAddress(ModbusArea Area, ushort Offset)
{
    /// <summary>人读形式，如 <c>HoldingRegister[100]</c>。只用于日志与错误信息 —— 它不是可回写的地址文本。</summary>
    public override string ToString() => $"{Area}[{Offset}]";
}
