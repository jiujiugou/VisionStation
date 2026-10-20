namespace VisionStation.App.Composition.Lifecycle;

/// <summary>Serilog 的日志收尾实现。薄到不需要测，只是为了把静态调用变成可替身。</summary>
public sealed class SerilogLogShutdown : ILogShutdown
{
    public void CloseAndFlush() => Serilog.Log.CloseAndFlush();
}
