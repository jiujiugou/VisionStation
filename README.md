# VisionStation — 金属零件视觉检测工作站

WPF 桌面工业视觉检测上位机。**判定交给 VisionMaster**（独立跑流程，经 TCP 把结果推给本程序），
本项目负责：工位状态机、结果落账（判定/数值/图入库 + 保留期）、产线连接（PLC 触发/回写/心跳）、无人值守（自检/看门狗/清理）。

> 2026-10 架构调整：自研算法工具链（模板匹配 / 霍夫圆 / Blob）已下线，判定归视觉平台。
> **2026-10-26 进一步收敛：YOLO/ONNX 推理移出运行时** —— App 不再引用 `Engines.Onnx`、不再随发布复制模型，
> 配方只认 `engine="Vm"`。ONNX 保留为离线验证工具（`tools/GoldenSetRunner` 跑黄金样本库），不属于产线运行时。
> 原因与取舍见 [docs/引擎集成-说明.md](docs/引擎集成-说明.md)；旧实现的回归数据见"黄金样本库"一节。

---

## 架构

```
VisionStation.slnx
│
├── VisionStation.Core/            契约与数据（无 IO、无 WPF、无算法实现）
│   ├── Engines/
│   │   ├── IInspectionEngine.cs   检测引擎唯一抽象
│   │   └── InspectionRequest.cs   一次检测的输入（图 + 触发来源 + 图路径）
│   └── Models/
│       ├── InspectionReport.cs    一次检测的结论（可入库/可追溯，带 Engine 字段）
│       ├── ToolResult.cs          单项结论 + DrawCommand 标注指令
│       ├── ResultKeys.cs          Locate / Measure / Defect 三个结果角色
│       └── RecipeConfig.cs        配方（sensor + engine + vm/onnx 参数）
│
├── VisionStation.Engines.Onnx/    ONNX YOLO 缺陷检测引擎
│   ├── YoloDetector.cs            纯推理：图 → 框（letterbox / 按类 NMS）
│   └── OnnxInspectionEngine.cs    框 → 报告（判定、摘要、标注）
│
├── VisionStation.Engines.Vm/      VM 结果解析（TCP 中转，不引用 SDK）
│   ├── VmInspectionEngine.cs      VmSolution → VmProcedure → 模块 IO 取结果
│   └── VmResultMapper.cs          读数 → 报告（纯逻辑，可单测）
│
├── VisionStation.Devices/         硬件抽象：相机（Hik / File / Webcam）、PLC、触发源
├── VisionStation.Storage/         EF Core + SQLite 记录、图片存储
├── VisionStation.Service/         启动自检、管家（清理）、配方存储
├── VisionStation.App/             WPF 组合根（MVVM + Messenger）
│   ├── Services/Inspection/       InspectionWorkflow（采图→引擎→存图→存库）
│   │                              InspectionEngineFactory（按配方选引擎，全系统唯一决策点）
│   └── Config/recipes/            配方文件（含 .active 指针）
└── VisionStation.Watchdog/        独立守护进程

tools/GoldenSetRunner/             黄金样本库跑批器（回归门禁）
tests/GoldenSet/                   数据集清单 + 基线快照
```

**依赖方向单向**：`Core ← Engines.Vm ← App`；`Core ← Engines.Onnx ← tools/GoldenSetRunner`（**App 不在其中**）；
`Core ← Devices ← Storage ← Service ← App`。
Core 不知道任何具体引擎，引擎不知道界面与数据库。

## 关键抽象

```
IInspectionEngine      2 个实现   运行时只用 VM 推送结果那个；ONNX 那个只服务离线跑批
ICamera                3 个实现   File / Webcam / Hikvision
ITriggerSource         2 个实现   Timer / PLC（Modbus TCP）
IDevice                2 个实现   Camera / Trigger
IInspectionRepository  1 个实现   SQLite（管家清理线程与检测线程共用，故按操作建 context）
```

`IInspectionEngine` 保留两个实现，但**只有 VM 进运行时**：ONNX 那个被 `tools/GoldenSetRunner` 调用。
契约共用仍有价值 —— 离线跑批和产线产出同一种 `InspectionReport`，两边数字才可比。
没有第二个实现的地方就不造接口：为假设中的变化付真实成本，是这类项目最常见的浪费。

## 检测链路

运行时**只有一条链路**：VM 判定 → TCP 推入 → 记账 → 回写 PLC。

```
PLC 置触发位（D100）→ 上位机读到上升沿并应答
    ↓
VisionMaster 独立跑流程（相机由 VM 独占，本程序不碰相机）
    ↓「发送数据」模块 → TCP :9100
VmResultListener → VmResultParser（分隔符 / JSON 两种格式 → InspectionReport）
    ↓
存图 + SQLite 入库 → 界面刷新 → PLC 结果回写（OK=1 / NG=2）+ 心跳自增
```

**本地主动检测路径（`RunOnceAsync`）已休眠**：它原本是"上位机取图 → 交给引擎推理"，
ONNX 移出运行时后，VM 模式下该方法直接返回 `null`，"开始检测"按钮只提示一句
"检测由 VisionMaster 执行"（见 `ControlPanelViewModel.StartDetection`）。
这条路径连同相机切换，是下一步要清理的遗留 —— 现在它不产生检测，也不该假装能。

**失败语义（一条硬规则）**：引擎不把异常抛给调用方。平台无授权、`.sol` 缺失、流程损坏，
一律返回 `IsOk=false` 且带原因的 NG 报告，并记 ERROR —— 产线要的是"这一件没判出来，且查得到原因"，
而不是进程消失。空结果集同样判 NG：**没检 ≠ 合格**。

## 运行前置

- **环境**：Windows 10/11 x64，.NET 8 SDK。
- **零硬件可跑**：不用真相机也不用真 PLC —— 用 `tools/VmSimulator` 往 9100 推几帧报文，
  整条链（解析 → 入库 → 界面 → 回写）就跑起来了；PLC 侧用汇川 AutoShop 离线仿真即可（见 `docs/产线闭环-说明.md`）。
- **VM 走 TCP 中转，不加载 SDK**：VisionMaster 独立跑流程，用「发送数据」模块把结果推到本程序
  的监听端口（`VmLink:Port`，默认 9100）。所以**任何机器 clone 下来都能 build**，不需要装 VM、不需要授权。
  联调时用 `tools/VmSimulator` 造几帧结果即可验证整条链。细节见 `docs/引擎集成-说明.md`。

## 运行

```bash
dotnet run --project src/VisionStation.App

# 黄金样本回归（105 张，约 23 秒，有回归时退出码 1）
dotnet run --project tools/GoldenSetRunner

# 打包（自包含）
dotnet publish src/VisionStation.App -c Release -r win-x64 --self-contained -o publish
```

## 配方文件

换检测产品 = 换一份配方 JSON，代码不动。引擎也写在配方里 —— .sol 与模型都是跟产品绑定的，
而 `appsettings.json` 描述的是这台机器（相机、PLC、存储）。

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
  },
  "onnx": { "modelPath": "models\\neu-det.onnx", "confidence": 0.25, "nms": 0.45 }
}
```

`sensor` 是防呆：分辨率对不上时拒绝检测并说明原因，而不是"照跑不误然后安静判错"。

## 健康检查

启动时执行 5 项，失败只写日志面板与状态栏，不弹模态框（无人值守时对话框会让看门狗失效）：

```
[✅] 设备连接: 2/2 在线
[✅] 数据库: visionstation.db
[✅] 存储路径: ...\InspectedImages
[✅] 检测引擎: Vm · washer.sol              ← 原来这里是"模板文件"
[✅] 配方文件: 金属垫圈检测 · Vm             ← 校验引擎名/参数/输出映射
```

## 操作

| 操作 | 方式 |
|------|------|
| 手动检测 | 点 **开始检测** |
| 自动检测 | 点 **自动模式**（定时器或 PLC 触发） |
| 急停 | 红色大按钮，有确认弹窗 |
| 图像缩放 | 滚轮 / F 键复位 |
| 切换产品 | 换配方文件（`.active` 指针决定用哪份）；ROI 与标定在 VM 客户端里调 |

ROI 编辑已从本程序移除：ROI 归 `.sol` 所有，在自己的界面里再画一遍等于制造第二份真相。

## 黄金样本库（回归验证）

```bash
dotnet run --project tools/GoldenSetRunner -- verify   # 校验数据集完整性
dotnet run --project tools/GoldenSetRunner             # 跑批 + 与基线对比
```

当前基线（`models/neu-det.onnx`，conf 0.25 / nms 0.45）：

| 指标 | 数值 |
| --- | --- |
| 命中（真值类检出） | 104 / 105（99.0%） |
| 错分（检出但认错类别） | 0 |
| 漏检（一个框都没有） | 1（`crazing_294`） |
| 跨类误报框 | 14 个 |

**2026-10-23 引擎重构后重跑：判定完全一致，误报框数量无变化。** 这条是重构安全性的直接证据 ——
同一份权重、同一套预处理，只是从"Pipeline + 工具"搬进"引擎"，结论一位没变。

## 技术栈

| 项 | 选择 |
|----|------|
| 运行时 / UI | .NET 8 · WPF · CommunityToolkit.Mvvm |
| 视觉平台 | 海康 VisionMaster 4.2（独立运行 + TCP 结果通信，可选） |
| 离线验证 | OpenCvSharp4 + ONNX Runtime（YOLOv8 导出模型，**仅 `tools/GoldenSetRunner`**） |
| 相机 | 海康 MVS SDK（真机 + 虚拟相机） |
| 存储 / 日志 / DI | SQLite + EF Core · Serilog · Microsoft.Extensions.DependencyInjection |

## 工程约束

- `ci.cmd`：构建 + 353 项测试（245 + 108）+ 分析器棘轮（当前基线 100 条，不许增长）。
- 测试不许为了让门禁变绿而放宽断言；改不动的测试只能删并说明原因。
- UI 改动必须实际启动走查（`docs/WPF开发规范.md` §13），build 绿不算完成。
