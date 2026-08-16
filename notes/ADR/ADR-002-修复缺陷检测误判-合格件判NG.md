# ADR-002 — 修复缺陷检测误判（合格件被判 NG）+ 回归测试

- **日期**：2026-08-16
- **状态**：完成
- **触发**：ADR-001 完成记录中留底的已知行为基线 —— `01_good`（合格件）因 Defect ROI 覆盖孔边缘被判 NG。对求职演示项目，合格件必须判 OK，否则"当场露馅"。

## 背景

`TestImages/01_good.jpg` 是一张 300×300 的合格垫圈（灰底 + 圆盘 + 中心圆孔）。端到端链路输出：

```
缺陷检测: [NG]  划痕:0 缺口:5 总数:5
```

根因：Defect ROI `(30,30,240,240)` 恰好覆盖整个圆盘（圆盘边缘与 ROI 边界重合），背景（暗）与孔（暗）经 `BinaryInv` 二值化后都变成白色 Blob —— 4 个背景角 + 1 个孔 = 5 个"缺口"，全部是误报。

## 目标（验收标准）

1. `01_good` → 缺陷检测 **OK**（合格件整体 OK）；
2. `02_bad_missing_hole` / `03_bad_scratched` → 缺陷检测 **NG**（缺陷件整体 NG）；
3. `dotnet build VisionStation.slnx` 零错误零警告；
4. 有回归测试锁定以上判定（防止以后改坏）。

## 非目标（本次不做）

- 不引入深度学习/新算法工具；
- 棋盘格标定、通信出口（PLC/TCP）、UI 量化指标等加分线仍留后续 ADR。

---

## 条目 1 — DefectTool 把零件背景和固有孔误报为缺陷

- **问题**：`DefectTool` 在 ROI 上直接二值化 + Blob 分析，没有区分"零件表面"与"背景/固有孔"。ROI 覆盖背景或孔时，背景边缘和孔被当成缺陷。
- **位置**：`VisionStation.Core/Tools/DefectTool.cs`
- **修复方向**（机制层面，不靠改 ROI 尺寸糊过去）：
  1. **忽略贴 ROI 边界的连通域**：背景区域必然与 ROI 边界相连，用 `ConnectedComponentsWithStats` 剔除贴边组件（符合工业惯例"ROI 边缘不完整区域不判缺陷"）；
  2. **新增可选"孔排除圆"参数**：`excludeX / excludeY / excludeRadius`（图像坐标，默认禁用），组件质心落入排除圆内的视为零件固有孔，跳过不判缺陷（对应产线"已知孔位，缺陷检测跳过孔区"的做法）。
- **消费点**：`Config/recipes/default.json`、`VisionStation.Test/Program.cs`、`App.xaml.cs` 兜底配方、`ToolFactory.CreateOne` 的 Defect 分支同步传入参数。
- **验证**：Test 全链路 3 图判定全部正确（01 OK、02/03 NG）。
- **状态**：已修复（实测：01_good 缺陷 OK、02/03_bad 缺陷 NG）

## 条目 2 — 缺 Pipeline 级"合格件/缺陷件"回归测试

- **问题**：现有单测只覆盖"单工具纯色图"（`DefectTool_CleanSurface_ReturnsOK` 用纯色图，永远测不出"背景+孔误报"这类问题）；没有端到端断言"合成合格垫圈整体 OK、缺陷垫圈整体 NG"。
- **位置**：`VisionStation.UnitTests/PipelineTests.cs`、`VisionStation.UnitTests/ToolTests.cs`
- **修复方向**：
  1. `ToolTests`：新增"垫圈表面（亮盘+暗孔+暗背景）无缺陷 → OK"用例，锁定孔/背景排除；
  2. `PipelineTests`：新增端到端用例 —— 用与 Test 项目一致的合成图（合格件 / 缺孔件 / 划痕件）跑完整 Pipeline，断言整体 OK/NG。
- **验证**：`dotnet test VisionStation.UnitTests` 全绿。
- **状态**：已修复（新增 4 个回归用例，`dotnet test` 12/12 通过）

---

## 完成记录（2026-08-16）

两条全部完成，验收标准实测通过：

- **合格件修复**：`DefectTool` 现在先用 `ConnectedComponentsWithStats` 剔除"贴 ROI 边界"的连通域（背景），再用新增的孔排除圆参数（`excludeX/excludeY/excludeRadius`，图像坐标）剔除零件固有孔。配方 `default.json`、App 兜底配方、`ToolFactory`、Test 均已同步参数。
- **链路验证**：`dotnet run --project VisionStation.Test` 3 图判定全部正确 —— `01_good` 缺陷 OK / 总判定 OK；`02_bad_missing_hole` 缺陷 NG（划痕 2）/ 总 NG；`03_bad_scratched` 缺陷 NG（划痕 1）/ 总 NG。
- **回归测试**：`ToolTests` 新增 3 例（合格垫圈表面带孔排除→OK、不带排除→NG 证明必要性、表面划痕→NG）；`PipelineTests` 新增端到端 1 例（合成合格件整体 OK、缺孔/划痕件整体 NG）。`dotnet test VisionStation.UnitTests` **12/12 通过**。
- **构建**：`dotnet build VisionStation.slnx` **0 错误 0 警告**。

### 后续候选（不在本次范围）

- `CoordinateMapperTests`（V1.5 清单缺口）：`CoordinateMapper` 在 App 层且依赖 WPF 类型（`Point/Rect`），补单测需先解耦到 Core（自建坐标结构）或让测试项目引用 App —— 留待后续 ADR 一并评估。

---

## 收尾规则

每修完一条：附验证 → 将对应条目状态改为"已修复" → 全部完成后把 ADR-002 状态改为"完成"，更新 worklog。
