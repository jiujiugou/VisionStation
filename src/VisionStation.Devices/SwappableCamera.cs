using VisionStation.Core.Models;

namespace VisionStation.Devices;

/// <summary>
/// 可热切换的相机包装器 —— 运行时在 File/Webcam/Hik 之间切换，无需重启
///
/// 为什么需要？
///   不是在启动时决定用什么相机——操作员可能在运行中切换。
///   SwappableCamera 持有一个 ICamera，对外暴露 ICamera 接口，
///   内部可以随时替换 _active 指针指向另一个实现。
///
/// 设计细节：
///   - cameraFactory 是委托（Func&lt;string, ICamera&gt;）而非直接引用 CameraFactory 类
///     → 规避 Devices 项目 → App 项目的循环依赖
///   - SwapTo 方法：创建新模式相机 → 替换 _active → 释放旧相机
/// </summary>
public class SwappableCamera : ICamera, IDisposable
{
    // 当前活跃的相机（所有 ICamera 方法委托给它）
    private ICamera _active;

    // 相机工厂委托（App 层注入，由 CameraFactory.CreateByMode 提供）
    private readonly Func<string, ICamera> _cameraFactory;

    // 当前模式名（UI 显示用）
    private string _currentMode = "File";

    // ══ ICamera 接口 — 全部委托给 _active ══
    public bool IsAvailable => _active.IsAvailable;
    public int FrameCount => _active.FrameCount;
    public string CurrentMode => _currentMode;

    /// <summary>可用相机类型列表（UI 下拉框用）</summary>
    public static readonly string[] AvailableModes = { "File", "Webcam", "Hikvision" };

    /// <param name="initial">启动时的默认相机</param>
    /// <param name="cameraFactory">委托：模式名 → 新 ICamera 实例</param>
    public SwappableCamera(ICamera initial, Func<string, ICamera> cameraFactory)
    {
        _active = initial;
        _cameraFactory = cameraFactory;
    }

    public void open() { if (_active is FileCamera fc) fc.RefreshFileList(); }
    public void close() { }
    public CameraFrame? Capture() => _active.Capture();

    /// <summary>
    /// 运行时切换到另一种相机模式。
    /// 创建新模式相机 → 替换 _active → 释放旧相机（如果旧相机实现 IDisposable）
    /// </summary>
    public void SwapTo(string mode)
    {
        var old = _active;
        _active = _cameraFactory(mode); // 委托调用 CameraFactory.CreateByMode
        _currentMode = mode;

        if (old is IDisposable d) d.Dispose();
    }

    public void Dispose()
    {
        if (_active is IDisposable d) d.Dispose();
    }
}
