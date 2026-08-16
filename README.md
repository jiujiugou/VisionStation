# VisionStation — 金属零件视觉检测工作站

WPF 桌面工业视觉检测软件。支持采图 → ROI 区域设定 → 多工具检测 Pipeline → 结果存储的完整链路。

---

## 架构

```
VisionStation.sln
│
├── VisionStation.App/          WPF 桌面应用（MVVM + Messenger）
│   ├── Views/                  界面（ImagePanel / ResultPanel / LogPanel / ControlPanel / StatusBar）
│   ├── ViewModels/             7 个 ViewModel，Messenger 解耦
│   ├── Services/
│   │   ├── DeviceManager.cs    设备管理器（IDevice 集合）
│   │   ├── HealthChecker.cs    启动自检（相机/数据库/存储/模板/配方）
│   │   └── Coordinates/
│   │       ├── CoordinateMapper.cs    屏幕 ↔ 图像双向换算
│   │       ├── ScreenCoordinate.cs    屏幕坐标（dp）
│   │       └── ImageCoordinate.cs     图像坐标（px）
│   ├── Converters/             值转换器
│   ├── Messages.cs             5 种 Messenger 消息类型
│   └── Config/recipes/         配方文件
│
├── VisionStation.Core/         纯算法，零 UI 依赖
│   ├── Tools/
│   │   ├── IVisionTool.cs      检测工具统一接口
│   │   ├── ToolResult.cs       统一产出 + DrawCommand 标注指令
│   │   ├── PresenceTool.cs     模板匹配
│   │   ├── DimensionTool.cs    霍夫圆 + 尺寸测量
│   │   └── DefectTool.cs       Blob 缺陷检测
│   ├── Pipeline.cs             工具链编排器
│   ├── Calibration.cs          px↔mm 换算
│   └── Models/                 数据模型（RecipeConfig / InspectionReport / RoiRegion）
│
├── VisionStation.Devices/      硬件抽象层
│   ├── IDevice.cs              设备统一接口
│   ├── ICamera.cs              相机接口
│   ├── ITriggerSource.cs       触发源接口
│   ├── CameraDevice / TriggerDevice   设备包装器
│   └── Camera/                 实现（FileCamera / WebcamCamera / HikCamera）
│
├── VisionStation.Storage/      数据持久化
│   ├── InspectionDbContext.cs  EF Core + SQLite
│   ├── InspectionRepository.cs 数据访问层
│   └── ImageStoreService.cs    图片存储（全存/仅NG/抽样）
│
└── VisionStation.Test/         端到端测试（合成图生成 + 全链路验证）
```

## 关键抽象（面试重点）

```
ICamera                        3 个实现  File / Webcam / Hik
ITriggerSource                 2 个实现  Manual / Timer
IVisionTool                    3 个实现  Presence / Dimension / Defect
IDevice                        2 个实现  Camera / Trigger
CoordinateMapper               1 个单例  屏幕 ↔ 图像 ↔ 世界
```

每个抽象遵循同一模式：**接口定义在 Core/Devices，实现在 App/Devices，DI 注入，互不依赖。**

## 检测链路

```
触发（手动/定时）
    ↓
Camera.Capture() → Mat 原图
    ↓
Pipeline.Run(image, rois)
    ├── Tool[0].Execute() → ToolResult { IsOk, Summary, DrawCommands }
    ├── Tool[1].Execute() → ToolResult
    └── Tool[2].Execute() → ToolResult
    ↓
DrawCommands → Cv2.Rectangle/Circle/DrawContours → 标注叠加图
    ↓
InspectionReport → SQLite + 图片文件
```

## 运行前置（干净机器）

从零开始跑通这个项目需要：

- **环境**：Windows 10/11 x64，安装 .NET 8 SDK（`dotnet --version` 确认版本 ≥ 8.0）。
- **无摄像头也能跑**：默认 `Camera:Mode=File`，直接从仓库内的 `TestImages/` 读取演示图，启动自检 5 项全过。
- **开箱即用**：克隆后直接 `dotnet run --project VisionStation.App` 即可，无需先跑 Test 生成图片（`TestImages/` 已随仓库提交）。
- **首次运行自动生成**：`visionstation.db`、`Logs/`、`InspectedImages/` 会在项目根目录自动创建（已加入 `.gitignore`，不影响仓库）。

## 运行

```bash
# 1. 生成测试图像 + 跑全链路
dotnet run --project VisionStation.Test

# 2. 启动 WPF 桌面应用
dotnet run --project VisionStation.App

# 3. 打包发布（自包含，不依赖 .NET 运行时）
dotnet publish VisionStation.App -c Release -r win-x64 --self-contained -o publish
```

## 配方文件

换检测产品只需要修改配方 JSON，不改代码：

```json
// Config/recipes/default.json
{
  "name": "金属垫圈检测",
  "calibration": { "pixelPerMillimeter": 15.0, "designedDiameterMm": 2.67 },
  "rois": [
    { "name": "Presence", "x": 120, "y": 120, "width": 80, "height": 80 }
  ],
  "tools": [
    { "type": "Presence", "enabled": true, "params": { "threshold": 0.70 } }
  ]
}
```

## 操作

| 操作 | 方式 |
|------|------|
| 手动检测 | 点 **开始检测** |
| 自动检测 | 点 **自动模式**（定时器触发） |
| 急停 | 红色大按钮，有确认弹窗 |
| ROI 编辑 | F2 进入 → 拖拽画框 → 拖拽移动 → 右下角缩放 |
| 删除 ROI | 选中后按 Delete / 右键 |
| 图像缩放 | 滚轮 / F 键复位 |
| 保存 ROI | 点工具栏 **保存** → 写入配方文件 |

## 健康检查

启动时自动执行 5 项检查：

```
[✅] 设备连接: 2/2 在线
[✅] 数据库: visionstation.db
[✅] 存储路径: D:\...\InspectedImages
[✅] 模板文件: D:\...\hole_template.png
[✅] 配方: 金属垫圈检测 · 3个工具
```

## 技术栈

| 项 | 选择 |
|----|------|
| 运行时 | .NET 8 |
| UI | WPF + CommunityToolkit.Mvvm |
| 视觉 | OpenCvSharp4 |
| 数据库 | SQLite + EF Core |
| 日志 | Serilog（文件 + 控制台 + UI 面板） |
| DI | Microsoft.Extensions.DependencyInjection |
| 通信 | WeakReferenceMessenger（ViewModel 间解耦） |

## V1.5 完成清单

- [x] 图像显示（Uniform 拉伸 + Zoom + 平移 + F键复位）
- [x] 鼠标像素坐标实时显示
- [x] 屏幕 ↔ 图像坐标双向换算（CoordinateMapper 独立）
- [x] Rectangle ROI（创建/删除/选中/拖动/缩放/JSON 保存）
- [x] IVisionTool 统一接口 + 3 个实现
- [x] Pipeline 遍历工具列表执行
- [x] DrawCommand 统一标注渲染
- [x] IDevice 设备抽象 + DeviceManager
- [x] 启动自检 5 项
- [x] 配方文件（ROI + Tool + 标定合一）
- [x] ITriggerSource + ICamera 设备抽象
- [x] 触发模式切换（手动/定时/将来 PLC）
- [x] MVVM + Messenger 解耦
- [x] 全局异常捕获 + 崩溃日志
- [x] Serilog 文件日志 + UI 告警面板
- [x] 并发保护（_isProcessing 锁）
- [x] 全链路端到端测试（合成图）
- [x] 单元测试（12 个，全部通过）
- [x] 部署打包验证（`dotnet publish -c Release -r win-x64 --self-contained`）
