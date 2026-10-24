namespace VisionStation.VmSimulator;

/// <summary>VmSimulator 的命令行参数。</summary>
internal sealed class Options
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9100;
    public int Count { get; set; } = 1;
    public int IntervalMs { get; set; } = 1000;
    public string Verdict { get; set; } = "OK";
    public string? ImagePath { get; set; }
    public string? RawJson { get; set; }

    /// <summary>true = 抓包模式（自己当服务端），false = 模拟发送</summary>
    public bool Listen { get; set; }

    /// <summary>抓包模式自动退出的秒数，0 = 一直等</summary>
    public int ListenTimeoutSec { get; set; }

    public double Diameter { get; set; } = 12.345;
}
