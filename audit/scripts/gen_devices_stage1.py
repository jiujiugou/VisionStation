#!/usr/bin/env python3
"""按 15 目标 × 9 类失效铺满 Devices 切片网格，生成 FailureCatalog。

为什么要脚本生成：135 格里绝大多数是「逐格判定为 N/A + 给理由」，
手写容易漏格或漏理由，而漏格必须由门禁发现、不能靠人眼。
脚本在写文件前自检：任何一格既没有 F 条目、没有 N/A 理由、也不在类级豁免里，直接报错退出。

用法：python audit/scripts/gen_devices_stage1.py
"""
import io
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "audit", "artifacts-devices")

CATS = ["无效", "缺失", "延迟", "重复", "失败", "阻塞", "崩溃", "资源耗尽", "恢复失败"]
NAMES = {
    "U08": "HikCamera", "U09": "SwappableCamera", "U10": "DeviceManager", "U11": "TimerTrigger",
    "U12": "FileCamera", "U13": "WebcamCamera", "U14": "CameraDevice", "U15": "TriggerDevice",
    "E05": "SwappableCamera→相机实现", "E06": "HikCamera→进程级SDK", "E07": "采图线程→Capture",
    "E08": "TimerTrigger→消费者", "E09": "CameraDevice→ICamera", "E10": "TriggerDevice→TimerTrigger",
    "E11": "设备→DeviceManager注册",
}
TARGETS = list(NAMES.keys())


def line_of(path, needle):
    with io.open(os.path.join(ROOT, path), encoding="utf-8-sig", errors="replace") as f:
        for i, ln in enumerate(f, 1):
            if needle in ln:
                return i
    raise SystemExit(f"锚点找不到: {path} :: {needle}")


def ref(path, needle):
    return f"{path}:{line_of(path, needle)}@{needle.strip()}"


# ───────────────────────── FOUND（MAIN，进阶段 2/3） ─────────────────────────
F = [
    dict(id="F-D01", t="U08", c="无效", reach="REACHABLE", dg="S1",
         anchor=("src/VisionStation.Devices/Camera/HikCamera.cs", "public bool IsAvailable =>"),
         inst=[("U13", "无效"), ("U12", "无效")],
         trig="相机断网/掉电/拔 USB；FileCamera 非循环模式读完全部图片",
         imp="IsAvailable 原先只在 Initialize 成功后置 true，此后没有任何路径把它置回 false：连续取帧失败、断线、SDK 缓冲耗尽都不会改变它。CameraDevice.IsConnected → DeviceManager.ConnectionStats → HealthChecker 一路报“在线”，界面显示“就绪 · N帧”，而 Capture 永远返回 null。检测静默停止，日志里没有一条指向设备。WebcamCamera 同构（构造时置真后不再更新），FileCamera 的 IsAvailable 只看文件总数、非循环耗尽后仍为真——三个相机的可用性判据都不可证伪。",
         disp="FIXED 2026-10-19, 见 V-D01：HikCamera 改为“开流成功 + 最近 3s 内有帧”双条件（FrameAlive 为纯函数，可单测），采图线程每帧刷新心跳；Webcam 改为连续读帧失败 3 次判离线；FileCamera 改为“有图且（循环 或 未读完）”"),

    dict(id="F-D02", t="U08", c="缺失", reach="REACHABLE", dg="S1",
         anchor=("src/VisionStation.Devices/Camera/HikCamera.cs", "public static Mat? TryConvertPixels"),
         inst=[],
         trig="换用 Mono16 / Bayer 工业相机（类注释已自认这两类未覆盖）",
         imp="像素格式判定不看 pixelType（参数被接收却未参与任何判断），而是用 data.Length/像素数 猜通道：Mono16 是 2 字节/像素，被当成 8UC1，等于把高位字节当像素值；4 字节像素（BGRA/Mono32）被当成 8UC3，只拷 pixels*3 字节，行跨直接错位。两种情况下图像尺寸、通道数全部合法，Pipeline 照常判 OK/NG——静默错误判定，现场无法发现，日志无异常。",
         disp="FIXED 2026-10-19, 见 V-D02：新增纯函数 TryConvertPixels，只接受 1/3 字节像素，其余返回 null 并给出原因，Capture 侧记 Error 后丢帧"),

    dict(id="F-D03", t="U08", c="失败", reach="REACHABLE", dg="S2",
         anchor=("src/VisionStation.Devices/Camera/HikCamera.cs", "打开设备失败"),
         inst=[],
         trig="设备被上一进程占用（0x80000203）或 MVS 客户端正连着——注释里写明这是“最容易失败的一步”",
         imp="步骤3 已成功 CreateDevice，步骤4 OpenDevice 失败后直接 return，没有 DestroyDevice：_camera 保留一个非空句柄，设备侧会话也没归还。启动期失败且程序继续运行时，这份残留正是本类注释里“上次没释放导致下次 0x80000203”的成因之一。取流失败（步骤6）路径同样漏了销毁。",
         disp="FIXED 2026-10-19, 见 V-D03：抽出 TeardownDevice()（StopGrabbing/CloseDevice/DestroyDevice 各自 try），步骤4/步骤6 失败路径与 Dispose 共用"),

    dict(id="F-D04", t="U08", c="崩溃", reach="CONSTRUCTIBLE", dg="S2",
         anchor=("src/VisionStation.Devices/Camera/HikCamera.cs", "Join(2000)"),
         inst=[],
         trig="关闭程序时采图线程正卡在 GetImageBuffer 的 500ms 等待里，或线程调度延迟超过 2s",
         imp="Join 的返回值被丢弃。超时后仍继续 StopGrabbing/CloseDevice/DestroyDevice 并 Finalize SDK，而采图线程下一次循环还要解引用 _camera —— 轻则 NullReferenceException 在无 catch 的后台线程上终止进程，重则 SDK 在已销毁句柄上取帧（原生访问违例，不可捕获）。",
         disp="FIXED 2026-10-19, 见 V-D04：Join 结果必须判断，超时则跳过销毁与 SDK 反初始化（宁可漏一次释放，由 3s 心跳超时兜底）并记 Error；_grabbing/_disposed 改 volatile"),

    dict(id="F-D05", t="E06", c="无效", reach="REACHABLE", dg="S2",
         anchor=("src/VisionStation.Devices/Camera/HikCamera.cs", "MV_CC_Finalize_NET"),
         inst=[("E06", "重复"), ("E06", "崩溃")],
         trig="热切换相机时新旧实例并存；或同进程创建第二个 HikCamera",
         imp="Dispose 无条件调用进程级 MV_CC_Finalize_NET。SDK 是进程级单例资源，反初始化会同时废掉其它仍存活实例的取流句柄；而 Initialize 侧没有引用计数，每个新实例都再 Initialize 一次。“关掉旧相机”于是等于“把新相机一起关掉”，且发生在切换成功之后，看起来像相机自己掉了。",
         disp="FIXED 2026-10-19, 见 V-D05：SdkAcquire/ReleaseSdk 做进程级引用计数，只有计数归零才 Finalize；失败路径归还引用"),

    dict(id="F-D06", t="E05", c="无效", reach="REACHABLE", dg="S2",
         anchor=("src/VisionStation.Devices/SwappableCamera.cs", "public bool SwapTo"),
         inst=[("U09", "无效"), ("E05", "失败"), ("E05", "恢复失败"), ("U09", "恢复失败")],
         trig="appsettings 的 Camera:Mode=Hikvision（当前配置即是），用户在相机下拉框选择“海康工业相机”",
         imp="两个问题叠加成一次破坏性操作：(1) _currentMode 硬编码 \"File\"，与实际注入的 HikCamera 不符，UI 的去重守卫 SelectedMode==CurrentMode 失效；(2) SwapTo 先创建新相机再释放旧相机，而工业相机是独占设备——旧实例还占着，新实例 OpenDevice 必然失败（构造函数吞掉异常不抛），随后旧实例被替换并 Dispose（连 SDK 一起 Finalize）。一次点击之后：旧相机已销毁、新相机从未就绪，日志却记“已切换 · Success”。",
         disp="FIXED 2026-10-19, 见 V-D06：SwapTo 返回 bool 并在候选不可用时回滚（不替换、不释放旧实例），CurrentMode 由 initialMode/实例类型推导；App 侧失败时记 Error 并回填下拉框"),

    dict(id="F-D07", t="U12", c="失败", reach="REACHABLE", dg="S2",
         anchor=("src/VisionStation.Devices/Camera/FileCamera.cs", "all = Directory.GetFiles"),
         inst=[],
         trig="配置的图片目录不可读（权限/被占用）、路径过长，或目录在扫描过程中被删除",
         imp="Directory.GetFiles 抛 UnauthorizedAccessException/IOException/DirectoryNotFoundException，构造函数里没有 try —— 异常从 CameraFactory.Create 抛到 DI 容器装配，应用直接启动失败。Directory.Exists 只挡住“不存在”，挡不住“存在但不可读”。原实现还在打日志时第二次调用 GetFiles，两次之间目录消失同样抛。",
         disp="FIXED 2026-10-19, 见 V-D07：扫描整体 try/catch，失败记 Error 并按空列表继续启动；日志复用同一次枚举结果"),

    dict(id="F-D08", t="U12", c="崩溃", reach="CONSTRUCTIBLE", dg="S2",
         anchor=("src/VisionStation.Devices/Camera/FileCamera.cs", "public void RefreshFileList"),
         inst=[],
         trig="采集线程正在 Capture 时，UI 线程调用 RefreshFileList（open()/Connect() 触发）",
         imp="刷新在原地 _files.Clear() 后重建，采集线程手里的列表会在 Count 检查通过之后被清空 → _files[_index] 越界抛 ArgumentOutOfRangeException；List 的 Count/内部数组在重建过程中也可能被读到中间态。异常发生在检测线程，表现为一次崩溃或一帧错图。当前 open() 无调用点，故判 CONSTRUCTIBLE。",
         disp="FIXED 2026-10-19, 见 V-D08：列表改为“整表替换”（构造新 List 后一次赋值），采集侧先取快照再操作；Publish 先归零下标再换引用"),

    dict(id="F-D09", t="E09", c="无效", reach="REACHABLE", dg="S2",
         anchor=("src/VisionStation.Devices/Device/CameraDevice.cs", "public void Connect"),
         inst=[("U14", "缺失"), ("U14", "恢复失败"), ("E09", "恢复失败"), ("U09", "缺失"),
               ("U15", "缺失"), ("E10", "缺失"), ("U15", "恢复失败"), ("E10", "恢复失败"),
               ("U08", "恢复失败")],
         trig="点相机页的“重新连接”按钮；或启动时 DeviceManager.ConnectAll 对相机执行 Connect",
         imp="包装层的 Connect/Disconnect 没有映射到被包装对象的 open/close：CameraDevice.Connect 只对 `_camera is FileCamera` 生效，而 DI 注入的是 SwappableCamera，判断永远为 false——按钮点了没有任何效果；SwappableCamera.open/close 是空转发（open 只刷文件列表、close 空实现）；HikCamera.open 复用 Initialize 却不复位 _disposed，再次 Dispose 会直接 return，设备会话永不释放；TriggerDevice.Connect 是空方法而 Disconnect 去 Stop 定时器，断开再连接后自动触发永久停摆。整条“设备失效 → 重连”链路四处断裂。",
         disp="FIXED 2026-10-19, 见 V-D09：CameraDevice 转发 open/close；SwappableCamera 转发到 _active；HikCamera.open 复位 _disposed 并先拆旧句柄；ITriggerSource 增加 IsRunning，TriggerDevice 用 _wasRunning 记忆并恢复"),

    dict(id="F-D10", t="U11", c="崩溃", reach="CONSTRUCTIBLE", dg="S2",
         anchor=("src/VisionStation.Devices/Trigger/TimerTrigger.cs", "Triggered?.Invoke()"),
         inst=[("U11", "失败"), ("E08", "崩溃")],
         trig="任一订阅者在 Triggered 处理器内同步抛异常（例如取帧时命中已释放的 Mat、绘图越界）",
         imp="try/finally 只保证 _busy 归零，不捕获异常。Elapsed 回调运行在 ThreadPool 线程，未捕获异常在那里等于进程终止——一个坏帧可以带走整个检测程序。项目在 HealthChecker.RunAll 里已经用单项 Safe 隔离承认了这类风险，触发源这层没有。",
         disp="FIXED 2026-10-19, 见 V-D10：回调改为 try/catch/finally，异常经 onError 出口上报，_busy 必定归零"),

    dict(id="F-D11", t="U10", c="无效", reach="CONSTRUCTIBLE", dg="S2",
         anchor=("src/VisionStation.Devices/DeviceManager.cs", "public bool AllConnected"),
         inst=[],
         trig="设备表为空：DI 装配漏注册，或测试/工具代码里直接 new DeviceManager()",
         imp="List.All 对空集合恒为 true → “0/0 全部在线”。这正是规程 §4 点名的“恒真判据、空表报全在线”。今天 HealthChecker 自己加了 total>0 守卫（F-038）才没暴露，但这个公开属性仍是同一个陷阱：任何新调用方都会踩。当前 AllConnected 无调用点，故判 CONSTRUCTIBLE。",
         disp="FIXED 2026-10-19, 见 V-D11：AllConnected 改为 Count>0 && All(...)，空表判 false"),

    dict(id="F-D12", t="E07", c="崩溃", reach="CONSTRUCTIBLE", dg="S2",
         anchor=("src/VisionStation.Devices/Camera/HikCamera.cs", "while (_grabbing)"),
         inst=[("E07", "失败"), ("E07", "资源耗尽")],
         trig="Marshal.Copy 或缓冲分配抛异常（OOM），或 SDK 返回异常 frameInfo（帧长≤0）",
         imp="采图循环整体没有 try/catch，也没有 finally 归还帧缓冲：异常直接终止后台线程 → 进程终止；若异常发生在 FreeImageBuffer 之前，SDK 内部帧缓冲永久泄漏，之后 GetImageBuffer 再也拿不到帧——“相机活着但不出图”，只能重启。",
         disp="FIXED 2026-10-19, 见 V-D12：取帧、拷贝各自 try/catch，FreeImageBuffer 移入 finally 必定归还；帧长非法抛给 catch 记日志并丢帧"),
]

# ─────────────────────── OBSERVATION（不计密度、不强制进阶段2） ───────────────────────
O = [
    dict(id="O-D01", t="U08", c="资源耗尽", reach="THEORETICAL", dg="S3",
         anchor=("src/VisionStation.Devices/Camera/HikCamera.cs", "ProcessExit += OnProcessExit"),
         inst=[],
         trig="同一实例多次成功 Initialize（open() 被调用）",
         imp="每次成功初始化追加一个 ProcessExit 订阅且从不移除，闭包持有 this → 旧实例连同 _latestFrame 无法回收。热切换下第二次 Initialize 必然失败（设备独占），实际累积次数有限，故列观察项。",
         disp="未修：建议改为延迟订阅一次并在 Dispose 中解除"),

    dict(id="O-D02", t="U09", c="崩溃", reach="THEORETICAL", dg="S3",
         anchor=("src/VisionStation.Devices/SwappableCamera.cs", "(old as IDisposable)?.Dispose()"),
         inst=[],
         trig="SwapTo 与 Capture 在不同线程同时执行",
         imp="替换 _active 后立刻 Dispose 旧实例：若另一线程正在 old.Capture()，会命中已释放的相机（ObjectDisposedException 或原生访问违例）。当前 Capture 与 SwapTo 都在 UI 线程上执行（OnTriggered 统一 Dispatcher 切换），故判理论可达。",
         disp="已加 _swapLock 保护换指针与释放顺序，Capture 路径未加锁；彻底解决需引用计数或读写锁"),

    dict(id="O-D03", t="E11", c="重复", reach="THEORETICAL", dg="S3",
         anchor=("src/VisionStation.Devices/DeviceManager.cs", "_devices.Add(device)"),
         inst=[],
         trig="对同一 IDevice 实例重复 Register",
         imp="Register 无去重、List 无同步：重复注册会让 ConnectionStats 的 total 虚高、ConnectAll 连接两次。当前注册只在 DI 容器构造期执行两行不同对象，也不存在并发注册，故为理论项。",
         disp="已补空值校验；去重未加（会改变语义），留观察项"),

    dict(id="O-D04", t="U11", c="恢复失败", reach="CONSTRUCTIBLE", dg="S3",
         anchor=("src/VisionStation.Devices/Trigger/TimerTrigger.cs", "_timer.Dispose()"),
         inst=[],
         trig="Dispose 之后，已在飞行中的 Elapsed 回调再触发一次",
         imp="停表与“回调完成”之间无同步：Dispose 后仍可能 Invoke 一次 Triggered，此时消费者可能已释放相机/窗口。后果是一次多余检测，不产生持续错误。",
         disp="已加 _disposed 标志（回调入口先判断），窗口缩小到标志读取与 Invoke 之间"),

    dict(id="O-D05", t="U15", c="无效", reach="REACHABLE", dg="S3",
         anchor=("src/VisionStation.Devices/Device/TriggerDevice.cs", "IsConnected => true"),
         inst=[],
         trig="任意时刻查看设备列表",
         imp="触发源包装的状态字段全是常量：IsConnected 恒真、Type 写死 \"PLC\"（实际是定时器）、Status 只按类型返回“自动模式”，不看定时器是否 Start。手动模式下界面也显示“自动模式”，现场排查会误判。",
         disp="未修：Type/Status 属展示语义，改动会影响 UI 文案；建议后续按 IsRunning 派生 Status"),

    dict(id="O-D06", t="U10", c="失败", reach="REACHABLE", dg="S3",
         anchor=("src/VisionStation.Devices/DeviceManager.cs", "单个失败不影响其他"),
         inst=[],
         trig="启动时相机未接（现场最常见状态），ConnectAll 里的 Connect 抛异常",
         imp="ConnectAll/DisconnectAll 用空 catch 吞掉一切异常，不记日志、不记哪台设备失败。现场排查 0x80000203 时唯一可用的线索（异常信息）被丢掉，只剩 HealthChecker 稍后报“1/2 在线”。这类缺陷不产生运行期可观测症状，只能靠人工审查发现，故作观察项。",
         disp="未修：加日志需给 DeviceManager 注入 ILogger（会改 DI 签名）；建议下一轮补 ConnectFailed 事件"),
]

# ───────────────────────── 类级豁免 ─────────────────────────
EX = [
    dict(category="延迟", applies_to=["U10", "U14", "U15", "E09", "E10", "E11"],
         reason="这些目标是无队列的同步查询/转发，调用即返回，不引入排队、缓冲或超时；延迟只可能来自它们调用的实现类，已由对应实现单元覆盖"),
    dict(category="阻塞", applies_to=["U14", "U15", "E09", "E10", "E11"],
         reason="这些目标不持锁、不做 IO、不等待外部事件，方法体内没有可阻塞点；阻塞面完全落在被包装实现或调用方一侧"),
    dict(category="资源耗尽", applies_to=["U10", "U14", "U15", "E05", "E08", "E09", "E10", "E11"],
         reason="这些目标不直接持有非托管句柄、线程、定时器或帧缓冲，只持有托管对象引用；资源的获取与释放全部发生在被包装的实现类内部"),
    dict(category="崩溃", applies_to=["U10", "U14", "U15", "E10"],
         reason="这些目标没有后台线程、原生调用或非托管内存操作，其代码路径只会抛托管异常并向上传递，不存在使进程终止或堆损坏的失效形态"),
]

# ───────────────────────── 逐格 N/A 理由 ─────────────────────────
NA = {
    ("U08", "延迟"): "取帧节奏由相机帧率决定；类内唯一的等待是 GetImageBuffer 的 500ms 超时与失败后的 5ms 退避，不构成排队延迟",
    ("U08", "重复"): "_hasNewFrame 在锁内消费后立即置 false，同一帧不会被 Capture 取走两次",
    ("U08", "阻塞"): "锁内只有一次整帧 Marshal.Copy，无 IO 无等待；读者最多等一次内存拷贝的时长",

    ("U09", "延迟"): "纯委托调用，无队列无缓冲，调用当次即返回当前相机的返回",
    ("U09", "重复"): "类内无重复执行同一动作的路径；同模式重复切换的去重属调用方职责，其失效已按同源记入 F-D06",
    ("U09", "失败"): "工厂抛异常或候选不可用时 _active 与 _currentMode 均保持不变，失败不污染状态",
    ("U09", "阻塞"): "SwapTo 内的等待来自候选实例的构造函数（相机初始化），边本身不引入额外等待",
    ("U09", "资源耗尽"): "资源由被包装相机持有并随 Dispose 链释放；本类不额外申请句柄、线程或缓冲",

    ("U10", "缺失"): "设备集合与状态汇总职责完整；动态增删/注销不在当前装配模型内（容器构造期一次性注册）",
    ("U10", "重复"): "两处注册语句注册的是不同设备对象；重复注册同一实例的后果属调用方误用，已按同源记入 O-D03",
    ("U10", "阻塞"): "ConnectAll 在容器构造期一次性串行执行，阻塞时长由各设备 Connect 决定，本类无锁无等待",
    ("U10", "恢复失败"): "本类只做状态汇总，不承担恢复职责；恢复入口在 UI 层（Reconnect 命令），其失效已在 F-D09 登记",

    ("U11", "无效"): "intervalMs 直接交给 System.Timers.Timer，0/负数在构造期抛 ArgumentException，非法值不会静默生效",
    ("U11", "缺失"): "Start/Stop/Fire/IsRunning 四个入口齐备，无缺失职责",
    ("U11", "延迟"): "间隔由 Timer 计时，回调在线程池上直接执行；本类不缓冲不排队，延迟由消费者承担",
    ("U11", "重复"): "CAS 保证同一时刻只有一个回调进入；Fire 绕过 CAS 是显式语义，其与定时叠加的后果已按同源记入 F-D10",
    ("U11", "阻塞"): "回调内不持锁不做 IO；_busy 只是原子标志，不阻塞任何线程",
    ("U11", "资源耗尽"): "只持有一个 System.Timers.Timer，数量恒定且 Dispose 释放，无累积增长路径",

    ("U12", "缺失"): "文件列表、循环/非循环、扩展名过滤三项职责完整；目录不可读的处理已按同源记入 F-D07",
    ("U12", "延迟"): "全部在内存列表上操作，唯一耗时是单张 ImRead 解码，无排队结构",
    ("U12", "重复"): "_index 单调推进，同一张图只在循环模式下按设计再次读到",
    ("U12", "阻塞"): "无锁无等待；刷新与采集通过整表替换解耦（见 F-D08）",
    ("U12", "资源耗尽"): "每次 Capture 交出一个 Mat，所有权随 CameraFrame 转给调用方；本类不持有帧缓冲",
    ("U12", "恢复失败"): "RefreshFileList 即恢复入口，可重扫目录；本类没有需要恢复的持久化状态",

    ("U13", "缺失"): "打开/关闭/读帧三件事齐备，close 之后可再次 open",
    ("U13", "延迟"): "VideoCapture.Read 同步阻塞到出帧或驱动超时，类内无额外排队",
    ("U13", "重复"): "每次 Read 取一帧并自增 FrameCount，无重复交付路径",
    ("U13", "失败"): "Read 失败返回 null（契约允许），连续失败计数到阈值即判离线",
    ("U13", "阻塞"): "Read 的阻塞时长由驱动决定，类内不持锁",
    ("U13", "崩溃"): "只持有一个 VideoCapture 句柄，close 幂等，无后台线程",
    ("U13", "资源耗尽"): "句柄数量恒为 1，close/Dispose 释放；无缓冲增长",
    ("U13", "恢复失败"): "close 后 open 可重建句柄；连续失败计数在成功读帧时归零，具备自恢复路径",

    ("U14", "无效"): "IsConnected 直接取 ICamera.IsAvailable，判据在相机实现侧（含帧心跳），本层不做二次推断",
    ("U14", "重复"): "Connect/Disconnect 是幂等转发，重复调用不产生叠加副作用",
    ("U14", "失败"): "转发调用不吞异常，失败会抛给调用方（DeviceManager 负责隔离）",

    ("U15", "重复"): "Connect/Disconnect 用 _wasRunning 记忆，重复 Connect 不会重复 Start",
    ("U15", "失败"): "被包装触发源的异常直接向上抛，本层不吞",

    ("E05", "缺失"): "委托面覆盖 open/close/Capture/IsAvailable/FrameCount，无遗漏成员",
    ("E05", "延迟"): "一次间接调用，中间没有缓冲或队列",
    ("E05", "重复"): "替换后旧实例立即 Dispose，不存在两个实例同时对外供帧",
    ("E05", "阻塞"): "SwapTo 内的等待来自候选实例的构造，边本身不引入额外等待",
    ("E05", "崩溃"): "委托链只做同步调用；跨线程 Dispose 的理论风险已在 O-D02 记录，不在此重复计数",

    ("E06", "缺失"): "Initialize 与 Finalize 已配对，并改为进程级引用计数（多实例只 Finalize 一次）",
    ("E06", "延迟"): "SDK 初始化是同步调用，无排队语义",
    ("E06", "失败"): "SDK 调用失败一律返回错误码并降级（IsAvailable=false），不向调用方抛",
    ("E06", "阻塞"): "MV_CC_Initialize_NET 为同步调用，阻塞发生在设备层，边本身无锁",
    ("E06", "资源耗尽"): "进程级引用计数与实例归还成对，不随实例数增长",
    ("E06", "恢复失败"): "SDK 初始化失败可由后续实例重新 Acquire，无需额外恢复状态",

    ("E07", "无效"): "每次交付的都是完整一帧快照拷贝，字段与像素数据在同一把锁内写入",
    ("E07", "缺失"): "取帧、归还缓冲、刷新心跳三件事在循环内齐备（含 finally 归还）",
    ("E07", "延迟"): "设计为“取最新帧”：检测慢时丢弃中间帧，是显式策略而非失效",
    ("E07", "重复"): "_hasNewFrame 保证一帧只被消费一次",
    ("E07", "阻塞"): "写者持锁 = 一次整帧拷贝，读者持锁 = 一次快照拷贝，无嵌套等待",
    ("E07", "恢复失败"): "线程退出后心跳超时使 IsAvailable 转 false，恢复走 open() 重建，不需要线程级恢复",

    ("E08", "无效"): "触发信号只承载“该拍一张”的语义，没有载荷需要校验",
    ("E08", "缺失"): "触发、停止、手动三个入口齐备",
    ("E08", "延迟"): "回调在 ThreadPool 上同步执行；排队与丢弃发生在消费者侧（_isProcessing 守卫），不在本边",
    ("E08", "重复"): "CAS 保证同一时刻只有一个回调在执行，其覆盖范围已在 F-D10 记录",
    ("E08", "失败"): "处理器异常由触发源 catch 并经 onError 上报，不再向 ThreadPool 逃逸",
    ("E08", "阻塞"): "本边不持锁：CAS 是原子标志不是锁",
    ("E08", "恢复失败"): "触发源无内部状态需要恢复，回调失败不影响下一次触发",

    ("E09", "缺失"): "ICamera 的五个成员全部转发，无遗漏",
    ("E09", "重复"): "同一时刻只有一个活跃相机，转发目标唯一",
    ("E09", "失败"): "转发不吞异常；相机不可用时 IsConnected 如实反映（含帧心跳）",
    ("E09", "崩溃"): "包装层只做同步转发，无后台线程；相机侧的异常在实现类内被拦截",

    ("E10", "无效"): "只读 IsRunning 并转发 Start/Stop，不改变触发语义",
    ("E10", "重复"): "Start/Stop 用 _wasRunning 与触发源内部状态双重约束，重复调用无叠加",
    ("E10", "失败"): "Stop/Start 的异常不吞，向上抛给调用方",

    ("E11", "无效"): "注册对象非空（Register 已加空值校验），具体类型由 DI 决定",
    ("E11", "缺失"): "无注销入口；热切换后包装对象不变（CameraDevice 包的是 SwappableCamera），不需要注销",
    ("E11", "失败"): "ConnectAll 逐个 try 隔离，单台失败不影响其余设备连接",
    ("E11", "崩溃"): "注册只在容器构造期执行，无并发遍历",
    ("E11", "恢复失败"): "连接失败不自动重试；恢复入口在 UI 层（Reconnect），已在 F-D09 登记",
}


def build():
    entries = []
    for e in F + O:
        entries.append({
            "id": e["id"],
            "target": f'{e["t"]} {NAMES[e["t"]]}',
            "category": e["c"],
            "status": "FOUND",
            "tier": "MAIN" if e in F else "OBSERVATION",
            "code_ref": ref(*e["anchor"]),
            "reachability": e["reach"],
            "danger_level": e["dg"],
            "trigger": e["trig"],
            "impact": e["imp"],
            "instances": [{"target": f"{t} {NAMES[t]}", "category": c} for t, c in e["inst"]],
            "disposition": e["disp"],
        })

    covered = set()
    for e in entries:
        covered.add((e["target"].split(" ")[0], e["category"]))
        for i in e["instances"]:
            covered.add((i["target"].split(" ")[0], i["category"]))
    exempt = {(t, x["category"]) for x in EX for t in x["applies_to"]}

    missing = [(t, c) for t in TARGETS for c in CATS if (t, c) not in covered and (t, c) not in exempt]
    no_reason = [m for m in missing if m not in NA]
    unused = [k for k in NA if k not in missing]
    if no_reason:
        for m in no_reason:
            print("缺格且无理由:", m)
        raise SystemExit(f"还有 {len(no_reason)} 格没有判定")
    if unused:
        raise SystemExit(f"NA 表有多余键（会造成重复覆盖）: {unused}")

    for i, (t, c) in enumerate(sorted(missing, key=lambda k: (TARGETS.index(k[0]), CATS.index(k[1]))), 1):
        entries.append({
            "id": f"N-D{i:02d}",
            "target": f"{t} {NAMES[t]}",
            "category": c,
            "status": "N/A",
            "na_reason": NA[(t, c)],
        })

    doc = {
        "slice": "devices",
        "generated": "2026-10-19",
        "scope_file": "audit/artifacts-devices/scope-dev.json",
        "index_ref": "audit/artifacts/index.json",
        "grid": f"{len(TARGETS)} 目标 × 9 类失效 = {len(TARGETS) * len(CATS)} 格",
        "coverage_claim": {
            "covered": [
                "8 个设备单元（相机三实现、热切换包装、设备管理器、触发器、两个 IDevice 包装）的类级行为",
                "7 条设备内部/跨层交互边：委托与生命周期、进程级 SDK 状态、采图线程与取帧、触发到消费者、包装转发、注册汇总",
                "跨线程面：采图线程 × Capture、Timer 回调 × 消费者、UI 刷新 × 采集",
                "外部依赖失效面：MVS SDK 句柄/会话、OpenCV VideoCapture/ImRead、文件系统",
            ],
            "not_covered": [
                "App 层 WPF 绑定与 Dispatcher 调度（消费者是 async void 这件事只能靠读代码确认，无法在本层断言）",
                "MVS SDK 内部实现与真实相机固件行为（无真机，只能按错误码与公开文档推断）",
                "USB 摄像头拔插的真实驱动行为（无设备，仅按失败计数建模）",
                "Watchdog 对宿主进程的监督与重启",
            ],
        },
        "entries": entries,
        "category_exemptions": EX,
    }

    os.makedirs(OUT, exist_ok=True)
    p = os.path.join(OUT, "FailureCatalog.json")
    with io.open(p, "w", encoding="utf-8", newline="\n") as f:
        json.dump(doc, f, ensure_ascii=False, indent=2)

    from collections import Counter
    main_f = [e for e in entries if e["status"] == "FOUND" and e["tier"] == "MAIN"]
    obs = [e for e in entries if e["status"] == "FOUND" and e["tier"] == "OBSERVATION"]
    print(f"条目 {len(entries)} | MAIN {len(main_f)} 条（全 FIXED）| OBSERVATION {len(obs)} 条 | N/A {len(entries) - len(main_f) - len(obs)} 条")
    print(f"覆盖 {len(covered)} 格 + 豁免 {len(exempt)} 格 = {len(covered) + len(exempt)} / {len(TARGETS) * len(CATS)}")
    print("危险级:", dict(Counter(e["danger_level"] for e in entries if e["status"] == "FOUND")))
    print("产物字节:", os.path.getsize(p))
    return p


if __name__ == "__main__":
    build()
    sys.exit(0)
