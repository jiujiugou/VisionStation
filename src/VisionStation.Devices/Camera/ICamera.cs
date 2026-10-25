using VisionStation.Core.Models;

namespace VisionStation.Devices;

/// <summary>
/// 相机抽象接口 —— VisionStation 设备层的核心抽象
///
/// 为什么需要接口？
///   工业视觉的核心矛盾：开发时没有相机，部署时相机型号不确定。
///   用接口把"拍照"这个动作抽象出来，不同实现互换时上层（工作流/引擎）无感知。
///
/// 三个实现：
///   FileCamera：开发阶段，从文件夹循环读图片
///   WebcamCamera：USB 摄像头，基于 OpenCV VideoCapture，5 行代码
///   HikCamera：海康工业相机，基于 MVS SDK，完整取流+线程管理
///
/// 所有实现都通过 SwappableCamera 包装，支持运行时热切换。
///
/// 为什么继承 IDisposable？
///   海康持有 SDK 会话句柄，不释放会导致下次启动 0x80000203 占用；
///   热切换时旧实例必须能被替换者释放。若靠"每个实现自己记得实现 IDisposable"，
///   新加一个相机忘记写就是运行时静默泄漏，编译器不会报。
///   继承进接口后，"可释放"从约定变成编译期强制 —— 比一条架构测试更硬，
///   也不用调用方到处写 `if (cam is IDisposable)`。
///
/// 注意：不要给 IDisposable.Dispose 写默认实现。那会让忘记实现的相机
/// 静默拿到空 Dispose，恰好把我们想防的泄漏放过去。
/// </summary>
public interface ICamera : IDisposable
{
    /// <summary>打开相机（连接/初始化）</summary>
    void Open();

    /// <summary>关闭相机（断开/释放资源）</summary>
    void Close();

    /// <summary>
    /// 采集一帧图像。
    /// 返回 null 表示本次采集失败（无可用图片、相机离线等），调用方应跳过本帧。
    /// </summary>
    CameraFrame? Capture();

    /// <summary>相机是否可用（在线 + 有图像源）</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 最近一次失败的原因，一句能直接照着排查的话（含错误码/路径/文件名）。
    ///
    /// 为什么必须有：现场排障时第一条要回答的是"它为什么不工作"。
    /// 没有这个属性时，真实原因只存在于实现类的日志里
    /// （FileCamera 知道"文件夹不存在"，HikCamera 知道 0x80000203 被独占），
    /// 而自检和状态栏只能说出"设备离线"——把人引向"线没插好"的错误方向。
    /// null = 当前没有未恢复的失败。
    /// </summary>
    string? LastError { get; }

    /// <summary>已采集帧数（累计）</summary>
    int FrameCount { get; }
}
