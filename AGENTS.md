# AGENTS.md — VisionStation

> 本文件是**索引**，不是手册。任何 AI 会话开始时先读它，需要细节再按指路去读 `docs/`。
> 本文件与实际代码冲突时，以代码为准，并回头修正本文件（不要留着一份过期的索引）。
> 控制在 200 行内。写长了等于没写。

## 1. 这是什么

WPF 金属零件视觉检测上位机。VisionMaster 跑流程出判定 → TCP 推入本程序 → 配方映射 → SQLite 存记录 → PLC 回写 → Serilog 落日志。

技术栈：.NET 8 · WPF · OpenCvSharp4 · EF Core + SQLite · xUnit · 海康 VisionMaster（**经 TCP 接收结果，不加载其 SDK**）。相机与 MVS 相关配置全在**平台侧**，本程序不引用任何相机库。

**2026-10-23 起的架构事实**（读代码前先认这几条，否则会找不存在的类）：
自研算法（模板匹配/霍夫圆/Blob）、`Pipeline`、`IVisionTool`、`Calibration`、`CoordinateMath`、ROI 编辑器**均已删除**。

**2026-10-27：单引擎（只有 VM），ONNX 整条线已删除。** YOLO/ONNX 引擎工程、离线跑批工具、模型权重、
黄金样本库一并移除，原因是没有生产调用者；配方只认 `engine="Vm"`
（`InspectionEngineFactory` 对其他值直接抛异常）。
理由：两个引擎并存在同一个程序里，读代码的人无法判断哪条是主线，而产线只跑 VM 这一条。

**2026-10-26：相机层整体删除**（`ICamera`/`HikCamera`/`FileCamera`/`WebcamCamera`/`CameraDevice`/`CameraFactory`/`CameraPage`）。
相机由 VM 独占，本程序**不采图、不存图、不做判定**：图由 VM 存盘，报文只带路径，本程序读图、标注、上屏。

**VM 不走 SDK（2026-10-23 定稿，别改回去）**：VM 4.2 的 SDK 是 .NET Framework 4.6.1 + Remoting，
在 .NET 8 里加载会崩（`E0000603` / `0xC000000D`），且要管理员权限与 VM 安装目录结构。
现在的做法是 **VisionMaster 独立跑流程 → 「发送数据」模块经 TCP 推一行结果 → 本程序解析入库**。
那一行有**两种格式**，由 `VmResultParser.Parse` 按首字符分流（`{` 走 JSON，否则走分隔符）：
- **分隔符**（现场最省事、当前在用）：`判定;值1;值2…`，没有字段名，值按配方 `vm.outputs` 顺序落位；
- **JSON**（要带图/逐项判定时用）：`{"verdict":"OK","imagePath":"...","items":[...]}`。
判定的可识别取值见 `VmResultParser.ParseVerdict`（注意 VM 的模块状态 **2 也是 NG**）。
`Engines.Vm` 里只有 `VmResultParser`（报文→报告）和 `VmExternalEngine`（推驱动占位），**不引用 SDK**。
通信端点在 `Devices/Vm/VmResultListener`。配置见 `docs/引擎集成-说明.md`。

## 2. 目录与禁区

| 路径 | 作用 | 能否修改 |
|---|---|---|
| `src/VisionStation.Core` | 契约与模型：`IInspectionEngine`、`InspectionReport`、`ToolResult`、`RecipeConfig`（**无 IO、无 WPF、无算法实现**） | 可改 |
| `src/VisionStation.Engines.Vm` | VM 报文解析（JSON / 分隔符→报告）+ 推驱动占位引擎；**不引用 VM SDK**，任何机器可编译 | 可改 |
| `src/VisionStation.Devices` | 触发源（Timer / PLC）、PLC 连接（TCP/RTU）、设备管理器。**相机层已删**（2026-10-26） | 可改 |
| `src/VisionStation.Storage` | EF Core DbContext、检测仓储、图片存储 | 可改 |
| `src/VisionStation.Service` | 自检（HealthChecker）、管家（Housekeeping） | 可改 |
| `src/VisionStation.App` | WPF 组合根、DI 注册、ViewModel | 可改 |
| `src/VisionStation.Watchdog` | 独立进程守护 | 可改 |
| `tests/VisionStation.UnitTests` + `tests/VisionStation.App.Tests` | 全部测试（当前 346 项：206 + 140，实测） | **只准加，不准改断言** |
| `tools/VmSimulator` | VM 报文模拟/抓包（**不在** `.slnx` 里，ci.cmd 不构建它） | 可改 |
| `scripts/rtu_slave.py` + `tools/RtuProbe` | Modbus RTU 从站模拟器 + 验证探针（需 com0com 虚拟串口对 COM3↔COM4，**不进 ci**，跑法见 `docs/产线闭环-说明.md` §8） | 可改 |
| `docs/` | 设计文档与规范；WPF 规则见 `WPF开发规范.md`，现状见 `WPF规范落地记录.md` | 可改 |
| `bin/` `obj/` `publish/` | 构建产物 | **禁止编辑** |
| `Logs/` `InspectedImages/` `TestImages/` `*.db` | 运行期数据 | **禁止编辑**，也不要加入示例代码 |

依赖方向单向：`Core ← Engines.Vm ← {App, UnitTests}`；
`Core ← Devices ← Storage ← Service ← App`。
Core 不得反向依赖任何工程；引擎不得依赖 App/Storage/Service。

## 3. 命令

**日常改动（秒级，改一次跑一次）**

```bat
dotnet build src\VisionStation.App -v q --nologo                                  :: 增量编译（含依赖链）
dotnet test  tests\VisionStation.App.Tests --filter "FullyQualifiedName~Plc" --nologo
dotnet test  tests\VisionStation.UnitTests --filter "FullyQualifiedName~Housekeeping" --nologo
```

**提交前（分钟级，跑一次算数）**

```bat
ci.cmd    :: 全量重建 src + 两个测试工程 → 分析器棘轮 → 346 项测试
```

`ci.cmd` 慢**是设计代价，不是故障**：三处 `--no-incremental` 全量重建，
因为 `scripts/analyzer_gate.py` 靠 build 日志里的完整警告清单判棘轮，
增量构建只报改动文件，棘轮会误判。所以它只在提交前跑。

**说"改完了"要贴什么证据**

| 改动性质 | 必须贴 |
|---|---|
| 单模块内部（一个 ViewModel、一个解析函数） | `dotnet build` 输出 + 相关测试 |
| 跨模块：`Core` 契约、`Storage` 实体/查询、`appsettings` 结构、一次改 >5 个文件 | `ci.cmd` 输出（退出码 0） |

中途遇到这三种情况也该跑一次 `ci.cmd`：改 `Core` 契约（编译挡得住签名，挡不住语义）、
一次改超 5 个文件、自己心里没底。其余时候**不要为了"确认一下"跑 ci**。

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

