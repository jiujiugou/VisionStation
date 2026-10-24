# AGENTS.md — VisionStation

> 本文件是**索引**，不是手册。任何 AI 会话开始时先读它，需要细节再按指路去读 `docs/`。
> 本文件与实际代码冲突时，以代码为准，并回头修正本文件（不要留着一份过期的索引）。
> 控制在 200 行内。写长了等于没写。

## 1. 这是什么

WPF 金属零件视觉检测工作站。取图 → 检测引擎判定（VisionMaster 流程 / ONNX 缺陷检测）→ 配方驱动 → SQLite 存记录 → Serilog 落日志。

技术栈：.NET 8 · WPF · OpenCvSharp4 · ONNX Runtime · EF Core + SQLite · xUnit · 海康 MVS SDK · 海康 VisionMaster（**经 TCP 接收结果，不加载其 SDK**）

**2026-10-23 起的架构事实**（读代码前先认这几条，否则会找不存在的类）：
自研算法（模板匹配/霍夫圆/Blob）、`Pipeline`、`IVisionTool`、`Calibration`、`CoordinateMath`、ROI 编辑器**均已删除**。
判定逻辑在引擎里：`IInspectionEngine` 两个实现，由配方 `engine` 字段选择。

**VM 不走 SDK（2026-10-23 定稿，别改回去）**：VM 4.2 的 SDK 是 .NET Framework 4.6.1 + Remoting，
在 .NET 8 里加载会崩（`E0000603` / `0xC000000D`），且要管理员权限与 VM 安装目录结构。
现在的做法是 **VisionMaster 独立跑流程 → 「发送数据」模块经 TCP 推一行 JSON → 本程序解析入库**。
`Engines.Vm` 里只有 `VmResultParser`（JSON→报告）和 `VmExternalEngine`（推驱动占位），**不引用 SDK**。
通信端点在 `Devices/Vm/VmResultListener`。配置见 `docs/引擎集成-说明.md`。

## 2. 目录与禁区

| 路径 | 作用 | 能否修改 |
|---|---|---|
| `src/VisionStation.Core` | 契约与模型：`IInspectionEngine`、`InspectionReport`、`ToolResult`、`RecipeConfig`（**无 IO、无 WPF、无算法实现**） | 可改 |
| `src/VisionStation.Engines.Onnx` | ONNX YOLO 缺陷检测引擎（`YoloDetector` 纯推理 + `OnnxInspectionEngine`） | 可改 |
| `src/VisionStation.Engines.Vm` | VM 结果解析（JSON→报告）+ 推驱动占位引擎；**不引用 VM SDK**，任何机器可编译 | 可改 |
| `src/VisionStation.Devices` | 相机（海康 / 文件 / 网页摄像头）、触发源 | 可改 |
| `src/VisionStation.Storage` | EF Core DbContext、检测仓储、图片存储 | 可改 |
| `src/VisionStation.Service` | 自检（HealthChecker）、管家（Housekeeping） | 可改 |
| `src/VisionStation.App` | WPF 组合根、DI 注册、ViewModel | 可改 |
| `src/VisionStation.Watchdog` | 独立进程守护 | 可改 |
| `tests/VisionStation.UnitTests` + `tests/VisionStation.App.Tests` | 全部测试（当前 218 项：143 + 75） | **只准加，不准改断言** |
| `docs/` | 设计文档与规范；WPF 规则见 `WPF开发规范.md`，现状见 `WPF规范落地记录.md` | 可改 |
| `bin/` `obj/` `publish/` | 构建产物 | **禁止编辑** |
| `models/` | ONNX 权重 | **禁止编辑** |
| `Logs/` `InspectedImages/` `TestImages/` `*.db` | 运行期数据 | **禁止编辑**，也不要加入示例代码 |

依赖方向单向：`Core ← Engines.{Onnx,Vm} ← App`，`Core ← Devices ← Storage ← Service ← App`。
Core 不得反向依赖任何工程；引擎不得依赖 App/Storage/Service。

## 3. 命令

```bat
ci.cmd                                                      :: 本地 CI 门禁：App 构建 + 测试 + 分析器棘轮 + 测试
dotnet build tests\VisionStation.UnitTests -v q --nologo    :: 仅编译检查，不是门禁
dotnet test  tests\VisionStation.UnitTests --nologo         :: 仅测试，不是门禁
dotnet test  tests\VisionStation.UnitTests --filter "FullyQualifiedName~Housekeeping" --nologo
```

`ci.cmd` 退出码 0 才算做完（这是唯一的门禁）；单独 `dotnet build` / `dotnet test` 不是门禁。任何"已完成"的说法，必须先贴门禁输出。

## 4. 硬规则（违反即作废重做）

1. **不许改测试让门禁变绿**。不许放宽断言、抬高阈值、加 `Skip`。测试红了就是代码错了。
2. **不许自己决定业务参数**：判定阈值、参数合法区间、保留天数、容量上限——一律问人。写"我按常见值取了 0.85"等于造假。
3. **每条结论必须可点开**：`文件:行号@符号`，文件要真存在、行号不许越界。引用旧代码前先确认它还在。
4. **修完追加不覆盖**：`V-xxx-retry`，原始 FAIL 记录保留。改了历史等于把"修了几次"抹掉。
5. **没做的写 NOT_RUN**。禁止"基本通过""应该没问题""已验证（无证据）"。
6. 一次性删改超过 5 个文件前，先说清楚改哪些、为什么。改完让人看 `git diff --stat`。
7. **WPF 构造函数零副作用**。连接设备、订阅事件、读写文件、发消息、起线程都不许放构造函数；用显式 `StartAsync`/`InitializeAsync`。纯值对象校验除外。
8. **View 不进 DI，VM 不引用 View**。View 由 WPF/DataTemplate/窗口工厂创建；VM 不得引用 `Application.Current`、`MessageBox`、`Dispatcher`、`Brush`、`Visibility`。
9. **UI 线程只做 UI**。OpenCV/DB/IO 下后台；跨线程位图必须 `Freeze()`；禁止 `.Result`/`.Wait()`；后台循环必须有取消令牌和异常拦截。
10. **导航是控制流，不是消息**。用 `INavigationService`，禁止 `NavigateMessage + Visibility` 拼页面。
11. **配置只在组合根读一次**。VM/Service 不得直接依赖 `IConfiguration`；业务参数仍按第 2 条问人。
12. **UI 变更必须实际启动走查**。build/test 绿不算完成；按 `docs/WPF开发规范.md` §13 走查并记录结果。

