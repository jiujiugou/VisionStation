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

    // 断开前记住自动模式是否在跑：Connect 是 Disconnect 的逆操作，
    // 旧实现 Connect 为空而 Disconnect 去 Stop，断开再连接后自动触发永久停摆，
    // 界面却仍显示"自动模式"。
    private bool _wasRunning;

    public void Connect()
    {
        if (!_wasRunning) return;
        _trigger.Start();
        _wasRunning = false;
    }

    public void Disconnect()
    {
        _wasRunning = _trigger.IsRunning;
        _trigger.Stop();
    }
}
