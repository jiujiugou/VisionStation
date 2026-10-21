namespace VisionStation.Devices.Plc;

/// <summary>Modbus 四个标准功能区</summary>
public enum ModbusArea
{
    /// <summary>线圈（0x），可读写。汇川 M/Y 变量映射到这里</summary>
    Coil,

    /// <summary>离散输入（1x），只读。汇川 X 物理输入映射到这里</summary>
    DiscreteInput,

    /// <summary>保持寄存器（4x），可读写。汇川 D 数据寄存器映射到这里</summary>
    HoldingRegister,

    /// <summary>输入寄存器（3x），只读。</summary>
    InputRegister,
}
