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
///   - SwapTo：创建新模式相机 → 校验可用性 → 替换 _active → 释放旧相机
///     **校验不通过就回滚**：不替换、不释放旧实例。工业相机是独占设备，
///     "新的没建成、旧的先拆了"等于把产线弄瞎。
///   - CurrentMode 必须反映真实相机类型：它同时被 UI 用来做"同一个模式不要重复切"的去重判断，
///     写死默认值会让守卫失效（配置 Mode=Hikvision 时界面仍认为是 File）。
/// </summary>
public class SwappableCamera : ICamera
{
    // 当前活跃的相机（所有 ICamera 方法委托给它）
    private ICamera _active;

    // 相机工厂委托（App 层注入，由 CameraFactory.CreateByMode 提供）
    private readonly Func<string, ICamera> _cameraFactory;

    // 当前模式名（UI 显示 + 去重判断用）
    private string _currentMode;

    // 切换互斥：换指针与释放旧实例不能交错
    private readonly object _swapLock = new();

    // ══ ICamera 接口 — 全部委托给 _active ══
    public bool IsAvailable => _active.IsAvailable;
    public int FrameCount => _active.FrameCount;
    public string CurrentMode => _currentMode;

    /// <summary>可用相机类型列表（UI 下拉框用）</summary>
    public static readonly string[] AvailableModes = { "File", "Webcam", "Hikvision" };

    /// <param name="initial">启动时的默认相机</param>
    /// <param name="cameraFactory">委托：模式名 → 新 ICamera 实例</param>
    /// <param name="initialMode">启动时的实际模式名；不传则按实例类型推断</param>
    public SwappableCamera(ICamera initial, Func<string, ICamera> cameraFactory, string? initialMode = null)
    {
        _active = initial ?? throw new ArgumentNullException(nameof(initial));
        _cameraFactory = cameraFactory ?? throw new ArgumentNullException(nameof(cameraFactory));
        _currentMode = string.IsNullOrWhiteSpace(initialMode) ? ModeOf(initial) : initialMode!;
    }

    /// <summary>按实例类型推断模式名（配置没给模式时的兜底）</summary>
    public static string ModeOf(ICamera camera) => camera switch
    {
        HikCamera => "Hikvision",
        WebcamCamera => "Webcam",
        FileCamera => "File",
        _ => "File"
    };

    // 转发到当前相机：open/close 不能是空实现，否则"连接/断开"在包装层就断了
    public void open() => _active.open();
    public void close() => _active.close();
    public CameraFrame? Capture() => _active.Capture();

    /// <summary>
    /// 运行时切换到另一种相机模式。
    /// 返回 false = 新模式不可用，已回滚（旧相机保持原样、未被释放）。
    /// </summary>
    public bool SwapTo(string mode)
    {
        lock (_swapLock)
        {
            ICamera candidate;
            try
            {
                candidate = _cameraFactory(mode);
            }
            catch (Exception)
            {
                return false;    // 工厂抛异常：旧相机不受影响，交给调用方报错
            }

            if (candidate == null)
                return false;

            // S2.5：工厂只造对象、不连接，所以"连上"这一步必须在这里做。
            // 切换的语义是"连上并验证可用"，不是"造了个对象就算切过去了"。
            // open 失败不抛（各实现内部自己 catch 并降级），可用性仍由 IsAvailable 判。
            candidate.open();

            if (!candidate.IsAvailable)
            {
                // 典型场景：旧海康还占着设备，新建的第二个实例必然 OpenDevice 失败。
                // 此时既不能替换 _active，更不能 Dispose 旧实例。
                candidate.Dispose();
                return false;
            }

            var old = _active;
            _active = candidate;
            _currentMode = mode;
            old.Dispose();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_swapLock)
        {
            _active.Dispose();
        }
    }
}
