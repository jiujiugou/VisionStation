# VisionStation — 金属零件视觉检测工作站

WPF 桌面工业视觉检测上位机。**判定交给 VisionMaster**（独立跑流程，经 TCP 把结果推给本程序），
本项目负责：工位状态机、结果落账（判定/数值/图入库 + 保留期）、产线连接（PLC 触发/回写/心跳）、无人值守（自检/看门狗/清理）。

> 2026-10 架构调整：自研算法工具链（模板匹配 / 霍夫圆 / Blob）已下线，判定归视觉平台。
> **2026-10-27：YOLO/ONNX 这条线已整条删除** —— 引擎工程、离线跑批工具、模型权重、黄金样本库一并移除，
> 原因是没有生产调用者；配方只认 `engine="Vm"`。
> 原因与取舍见 [docs/引擎集成-说明.md](docs/引擎集成-说明.md)。

---

## 架构

```
VisionStation.slnx
│
├── VisionStation.Core/            契约与数据（无 IO、无 WPF、无算法实现）
│   ├── Engines/
│   │   └── IInspectionEngine.cs   检测引擎唯一抽象（同文件里放着 InspectionRequest：一次检测的输入）
│   └── Models/
│       ├── InspectionReport.cs    一次检测的结论（可入库/可追溯，带 Engine 字段）
│       ├── ToolResult.cs          单项结论 + DrawCommand 标注指令
│       ├── ResultKeys.cs          Locate / Measure / Defect 三个结果角色
│       └── RecipeConfig.cs        配方（sensor + engine + vm 参数）
│
├── VisionStation.Engines.Vm/      VM 结果解析（TCP 中转，不引用 SDK）
│   ├── VmResultParser.cs          报文 → 报告（纯逻辑，可单测）
│   ├── VmResultMessage.cs         报文的 DTO（只服务 JSON 那条解析路）
│   └── VmExternalEngine.cs        推驱动占位引擎（不检测、不加载 SDK）
│
├── VisionStation.Devices/         硬件抽象：触发源、PLC 连接（相机归平台独占，本程序不采图）
├── VisionStation.Storage/         EF Core + SQLite 记录、图片存储
├── VisionStation.Service/         启动自检、管家（清理）、配方存储
├── VisionStation.App/             WPF 组合根（MVVM + Messenger）
│   ├── Services/Inspection/       InspectionWorkflow（接收报文→读图→标注上屏→入库→回写）
│   │                              InspectionEngineFactory（按配方选引擎，全系统唯一决策点）
│   └── Config/recipes/            配方文件（含 .active 指针）
└── VisionStation.Watchdog/        独立守护进程
```

**依赖方向单向**：`Core ← Engines.Vm ← App`；
`Core ← Devices ← Storage ← Service ← App`。
Core 不知道任何具体引擎，引擎不知道界面与数据库。

## 关键抽象

```
IInspectionEngine      1 个实现   VmExternalEngine（推驱动，不检测、不加载 SDK）
ITriggerSource         2 个实现   Timer / PLC（Modbus TCP / RTU）
IDevice                1 个实现   Trigger（相机层 2026-10-26 已删）
IInspectionRepository  1 个实现   SQLite（管家清理线程与检测线程共用，故按操作建 context）
```

没有第二个实现的地方就不造接口：为假设中的变化付真实成本，是这类项目最常见的浪费。

## 检测链路

运行时**只有一条链路**：VM 判定 → TCP 推入 → 读图标注上屏 → 记账 → 回写 PLC。

```
PLC 置触发位（D100）→ PlcTriggerSource 读到上升沿
    ├─ 清 D100（应答）+ 心跳自增（D102）
    └─ Triggered(Plc) → InspectionWorkflow.RunOnceAsync —— 空实现，恒返回 null
    ↓
VisionMaster 自行跑流程（相机由 VM 独占，本程序不碰相机，也不发起检测）
    ↓「发送数据」模块 → TCP :9100
VmResultListener → VmResultParser（分隔符 / JSON 两种格式 → InspectionReport）
    ↓
读 VM 存盘的图（报文只带路径）→ 标注 → 上屏 → SQLite 入库 → PLC 结果回写（OK=1 / NG=2）
```

> **已知设计缺口：D100 这条链路到 `RunOnceAsync` 就断了。** 该方法无条件返回 `null`，
> 不采图、不判定、不入库（`InspectionWorkflow.cs:93`）。所以 D100 现在只剩"握手 + 让 PLC 侧
> 累计触发计数"的作用，D101 只随 VM 主动推来的帧变化 —— **本程序不发起检测，检测由 VM 侧自行触发**。
> 两种修法（按引擎分流触发源 / 文档承认现状）尚未拍板，别在这里擅自补一段"触发即检测"。

**本地主动检测路径已删除**：相机层（`ICamera` + Hik/File/Webcam 三个实现 + 热切换）
于 2026-10-26 整体移除 —— 相机归视觉平台独占，本程序不再采图。
`RunOnceAsync` 因此保留为空实现（见上），"开始检测"按钮只提示
"检测由 VisionMaster 执行"（见 `ControlPanelViewModel.StartDetection`），点了不会有检测发生。
"重新连接相机"按钮、相机页、"相机"指示灯一并删除：灯换成"平台"，反映平台推没推结果。

**失败语义（一条硬规则）**：引擎不把异常抛给调用方。平台无授权、`.sol` 缺失、流程损坏，
一律返回 `IsOk=false` 且带原因的 NG 报告，并记 ERROR —— 产线要的是"这一件没判出来，且查得到原因"，
而不是进程消失。空结果集同样判 NG：**没检 ≠ 合格**。

## 运行前置

- **环境**：Windows 10/11 x64，.NET 8 SDK。
- **零硬件可跑**：不用真相机也不用真 PLC —— 用 `tools/VmSimulator` 往 9100 推几帧报文
  （`--image` 指到 `TestImages/` 下的一张图，报文里带路径，本程序才读得到图），
  整条链（解析 → 读图标注 → 入库 → 界面 → 回写）就跑起来了；PLC 侧用汇川 AutoShop 离线仿真即可（见 `docs/产线闭环-说明.md`）。
  没有 File 相机这回事了：相机层随 2026-10-26 整体删除，离线验图走的就是这条模拟推送。
- **VM 走 TCP 中转，不加载 SDK**：VisionMaster 独立跑流程，用「发送数据」模块把结果推到本程序
  的监听端口（`VmLink:Port`，默认 9100）。所以**任何机器 clone 下来都能 build**，不需要装 VM、不需要授权。
  联调时用 `tools/VmSimulator` 造几帧结果即可验证整条链。细节见 `docs/引擎集成-说明.md`。

## 运行

```bash
dotnet run --project src/VisionStation.App

# 打包（自包含）
dotnet publish src/VisionStation.App -c Release -r win-x64 --self-contained -o publish
```

## 配方文件

换检测产品 = 换一份配方 JSON，代码不动。引擎也写在配方里 —— `.sol` 是跟产品绑定的，
而 `appsettings.json` 描述的是这台机器（PLC、存储、平台端口）。

```json
{
  "name": "金属垫圈检测",
  "schemaVersion": 2,
  "sensor": { "width": 1280, "height": 1024 },
  "engine": "Vm",
  "vm": {
    "solutionPath": "Config\\solutions\\washer.sol",
    "procedure": "流程1",
    "imageSourceModule": "图像源1",
    "outputs": [
      { "module": "圆查找1", "label": "外径", "role": "Measure",
        "io": "Radius", "ioKind": "Circle", "metric": "Radius_px", "unit": "px" }
    ]
  }
}
```

`sensor` 原本是防呆（分辨率对不上时拒绝检测并说明原因），但**相机层删除后已不生效**：本程序不采图，没有可比对的帧尺寸，`RecipeConfig.CheckSensor` 现在只剩单测在调（`RecipeSensorTests`）。字段保留是为了将来若恢复自采图时不必改配方格式。

## 健康检查

启动时执行 5 项，失败只写日志面板与状态栏，不弹模态框（无人值守时对话框会让看门狗失效）：

```
[✅] 设备连接: 1/1 在线                      ← 相机层删除后只剩触发源
[✅] 数据库: visionstation.db
[✅] 存储路径: ...\InspectedImages
[✅] 检测引擎: Vm · washer.sol              ← 原来这里是"模板文件"
[✅] 配方文件: 金属垫圈检测 · Vm             ← 校验引擎名/参数/输出映射
```

## 操作

| 操作 | 方式 |
|------|------|
| 手动检测 | 本程序不发起：点 **开始检测** 只写一条日志"检测由 VisionMaster 执行"（`ControlPanelViewModel.cs:131`），不检测、不入库 |
| 自动检测 | 点 **自动模式**（定时器 / PLC 触发）—— 节拍与握手照走，但仍不发起检测，结果只来自 VM 推送 |
| 急停 | 红色大按钮，有确认弹窗 |
| 图像缩放 | 滚轮 / F 键复位 |
| 切换产品 | 换配方文件（`.active` 指针决定用哪份）；ROI 与标定在 VM 客户端里调 |

ROI 编辑已从本程序移除：ROI 归 `.sol` 所有，在自己的界面里再画一遍等于制造第二份真相。

## 技术栈

| 项 | 选择 |
|----|------|
| 运行时 / UI | .NET 8 · WPF · CommunityToolkit.Mvvm |
| 视觉平台 | 海康 VisionMaster 4.2（独立运行 + TCP 结果通信，可选） |
| 图像读写 / 标注 | OpenCvSharp4（读 VM 存盘的图、画标注） |
| 相机 | **不在本程序**：由 VisionMaster 独占（本程序只接收图路径与判定） |
| 存储 / 日志 / DI | SQLite + EF Core · Serilog · Microsoft.Extensions.DependencyInjection |

## 工程约束

- `ci.cmd`：构建 + 379 项测试（221 + 158）+ 分析器棘轮（当前基线 100 条，不许增长）。
- 测试不许为了让门禁变绿而放宽断言；改不动的测试只能删并说明原因。
- UI 改动必须实际启动走查（`docs/WPF开发规范.md` §13），build 绿不算完成。
