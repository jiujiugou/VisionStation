namespace VisionStation.Devices;

/// <summary>
/// 触发源设备 —— 包装 ITriggerSource，实现 IDevice
/// </summary>
public class TriggerDevice : IDevice
{
    private readonly ITriggerSource _trigger;

    public string Name => "触发源";
    public string Type => "PLC";

    // 触发源没有连接状态——手工和定时器始终可用，将来PLC才需要
    public bool IsConnected => true;

    public string Status => _trigger switch
    {
        TimerTrigger => "自动模式",
        _ => "手动模式"
    };

    public TriggerDevice(ITriggerSource trigger) => _trigger = trigger;

    public void Connect() { }
    public void Disconnect() { _trigger.Stop(); }
}
