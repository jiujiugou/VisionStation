# WPF 规范落地记录

> **这份文件只写现状。** 规则全文见 `docs/WPF开发规范.md`，两份文件不要互相掺内容。
>
> 记录方式：只追加、不覆盖。已修复的条目保留原始描述，状态改为 `FIXED` 并补上验证证据；
> 重新打开时追加 `W-xx-retry`，不删除原始记录。禁止在这里写“基本完成”“应该没问题”。

---

## 1. 基线

- **建立日期**：2026-10-20
- **基线提交**：`1aec8b5`（App 层三步分析 189 格）之后的工作区
- **范围**：`src/VisionStation.App` 为主的 WPF 生命周期、组合根、VM/View 契约、线程模型、
  配置路径、资源与测试；Core/Devices/Storage/Service 的算法与设备逻辑不在本次范围内。
- **P0 交付**：
  - `docs/WPF开发规范.md`（规则）
  - `docs/WPF规范落地记录.md`（本文件）
  - `docs/ADR-004-WPF开发规范与迁移路线.md`（选型与路线；`notes/` 被 `.gitignore:36` 忽略，
    新 ADR 不放那里）
  - `AGENTS.md` 索引与 WPF 硬规则
- **P0 不包含**：任何业务代码、测试断言、审计产物的修改。

---

## 2. 现状快照

当前 App 层的实际形态：

- 组合根：`App.OnStartup` 一个方法内完成看门狗、Serilog、配置、配方、DI、自检、建库、开窗
  （`src/VisionStation.App/App.xaml.cs:36-129`）。
- DI：除少数按接口注册的组件外，View、ViewModel、MainWindow 全部 Singleton
  （`src/VisionStation.App/Services/ServiceCollectionExtensions.cs:141-165`）。
- View 创建：`MainWindow` 构造函数注入 8 个 View，再逐个赋给 `ContentControl.Content`
  （`src/VisionStation.App/MainWindow.xaml.cs:15-44`）。
- 导航：`SidebarViewModel` 发 `NavigateMessage`，`MainWindow` code-behind 改 `Visibility`
  （`src/VisionStation.App/ViewModels/SidebarViewModel.cs:49-53`、
  `src/VisionStation.App/MainWindow.xaml.cs:46-54`）。
- 主工作流：`MainViewModel` 在 UI 线程上完成采图、Pipeline、标注、存图、存库、统计、消息
  （`src/VisionStation.App/ViewModels/MainViewModel.cs:132-235`）。
- 测试：`VisionStation.UnitTests` 目标框架 `net8.0`，未引用 App
  （`tests/VisionStation.UnitTests/VisionStation.UnitTests.csproj:4,27-30`），因此 App 层当前
  **没有自动化测试**。

---

## 3. 偏差清单

优先级：`P1` = 先做，风险/收益比最高；`P2` = 生命周期与组合；`P3` = 线程与工作流；
`P4` = 配置与路径；`P5` = 资源、命名与注释。状态：`OPEN` / `FIXED` / `NOT_RUN`。

| ID | 规则 | 现状与证据 | 影响 | 优先级 | 状态 |
|---|---|---|---|---|---|
| W-01 | WPF-L1.2 | 构造函数产生副作用：`HikCamera` 构造即连接（`src/VisionStation.Devices/Camera/HikCamera.cs:71-75`）；`DeviceManager` 在 DI 工厂里 `ConnectAll`（`src/VisionStation.App/Services/ServiceCollectionExtensions.cs:69-76`）；`MainViewModel` 构造内订阅触发并发消息（`src/VisionStation.App/ViewModels/MainViewModel.cs:97-117`）；多个 VM 构造内注册 Messenger（`LogPanelViewModel.cs:37-48`、`ResultPanelViewModel.cs:41-84`） | 启动顺序由“谁先解析”决定；VM 无法脱离 UI 测试 | P2 | OPEN |
| W-02 | WPF-L1.2 / L1.3 | 相机启动被初始化两次：`SwappableCamera` 构造时 `HikCamera` 已 `Initialize`（`ServiceCollectionExtensions.cs:57-63`、`HikCamera.cs:71-75`），随后 `ConnectAll` 的 `open()` 又 `TeardownDevice + Initialize`（`HikCamera.cs:387-394`） | 启动变慢；设备被关闭/重开的失败窗口翻倍 | P2 | OPEN |
| W-03 | WPF-D1.2 / V1.1 | View 与 ViewModel 全部 Singleton，`MainWindow` 注入 8 个 View（`ServiceCollectionExtensions.cs:141-165`、`MainWindow.xaml.cs:15-44`） | View 生命周期脱离窗口；无法用 DataTemplate/设计时；页面 VM 无法按页面释放 | P2 | OPEN |
| W-04 | WPF-N1.1 / A1.5 | 导航用 Messenger + `Visibility`：`SidebarViewModel.cs:49-53` 发 `NavigateMessage`，`MainWindow.xaml.cs:46-54` 改可见性，消息定义在 `Messages.cs:68-75` | 控制流分散；页面状态与可视树两套真相；导航不可单测 | P2 | OPEN |
| W-05 | WPF-V1.3 | `MainWindow` code-behind 同时承担导航、F2 编辑模式、标题与可见性切换（`MainWindow.xaml.cs:46-80`） | 视图边界模糊，后续功能会继续往 code-behind 堆 | P2 | OPEN |
| W-06 | WPF-A1.2 / A1.3 | `MainViewModel` 同时做采图、Pipeline、标注、存图、存库、统计、消息（`MainViewModel.cs:143-235`），注释仍写“只做这一件事”（`MainViewModel.cs:22`） | VM 过大；工作流无法复用/测试；注释与代码不符 | P3 | OPEN |
| W-07 | WPF-T1.1 / T1.2 | 整条检测链路在 UI 线程执行：`OnTriggered` 切到 Dispatcher（`MainViewModel.cs:132-138`），`ProcessFrame` 内跑 `_pipeline.Run`（`MainViewModel.cs:171`）与 `SaveAsync`（`MainViewModel.cs:206`） | 检测/存库慢时界面卡死；自动运行下更明显 | P3 | OPEN |
| W-08 | WPF-T1.3 | `MatToBitmap` 未 `Freeze()`（`MainViewModel.cs:285-310`） | 位图不能安全跨线程；阻塞把转换移到后台 | P3 | OPEN |
| W-09 | WPF-T1.5 | 日志面板用阻塞 `Dispatcher.Invoke`（`LogPanelViewModel.cs:53`），并每条日志 `Entries.Refresh()`（`LogPanelViewModel.cs:62`） | 高频日志时 UI/后台互相等待，面板卡顿 | P3 | OPEN |
| W-10 | WPF-E1.2 / V1.3 | `ControlPanel` 已迁到 `IDialogService`（`ControlPanelViewModel.cs:119`）；`RoiEditorViewModel` 保存已改为 `IRecipeStore + IDialogService`（`RoiEditorViewModel.cs:291-321`） | ViewModels 下已无 `MessageBox`；保存成功/失败可测 | P1 | FIXED |
| W-11 | WPF-A1.2 / B1.4 | 结果面板写死 4 个工具名与判定方式（`ResultPanelViewModel.cs:49-79`），并输出十六进制颜色（`ResultPanelViewModel.cs:36,83,103`） | 配方工具一改 UI 就错；主题无法统一切换 | P3 | OPEN |
| W-12 | WPF-V1.4 / B1.1 | 旧实现同时有 XAML 绑定 `Source="{Binding CurrentImage}"`（`ImagePanelView.xaml:86`）与 code-behind 手动赋值（`ImagePanelView.xaml.cs:25-29`）；现已删除手动写入 | 同一属性双写已消除；切 Tab 哈希验证通过 | P1 | FIXED |
| W-13 | WPF-E1.1 | 日志双轨：Serilog 写控制台/文件（`App.xaml.cs:74-81`），UI 面板另靠 `LogMessage`（`Messages.cs:39-47`、`LogPanelViewModel.cs:37-48`） | 系统日志进不了 UI；两套记录对不上 | P3 | OPEN |
| W-14 | WPF-C1.1 / C1.3 | 配置读取散落：`AppPaths.cs:41-46`、`CameraFactory.cs:29,43,44`、`ServiceCollectionExtensions.cs:62,67,93,132-137`；`Camera:Mode` 读两次（`CameraFactory.cs:29`、`ServiceCollectionExtensions.cs:62`）；`reloadOnChange` 无消费者（`App.xaml.cs:67`）；`appsettings.json:9` 的 `Trigger:Mode` 无人读取 | 默认值多处漂移；改配置可能不生效 | P4 | OPEN |
| W-15 | WPF-C1.2 | 可写数据基于 `Environment.CurrentDirectory` 解析（`AppPaths.cs:63-80`），数据库/日志/图片落在启动目录 | 快捷方式、计划任务、服务方式启动时路径不可控 | P4 | OPEN（需决策） |
| W-16 | WPF-R1.1 / R1.2 / R1.3 | 资源全堆在 `App.xaml:6-114`；视图内仍有硬编码颜色（如 `SidebarView.xaml:4`、`CameraPageView.xaml:4-5`）；转换器重复注册（`App.xaml:20` 与 `StatusBarView.xaml:7`） | 主题不一致；改色要全仓搜；资源键冲突 | P5 | OPEN |
| W-17 | WPF-TS1.1 / A1.6 | 旧：App 层零测试，测试工程 `net8.0` 且未引用 App；现有 `tests/VisionStation.App.Tests`（7 个测试），UnitTests 195 + App.Tests 7 = 202 | 关键 VM 命令已有护栏；生命周期/导航测试仍待 S2 扩展 | P1 | FIXED |
| W-18 | WPF-S1.1 | `ICamera` 方法小写 `open/close`（`src/VisionStation.Devices/Camera/ICamera.cs:32,35`） | 契约与其余 PascalCase API 不一致 | P5 | OPEN |
| W-19 | WPF-S1.3 | 业务代码内堆积事故编号/修复史（15 处 `修复（F-...）`，如 `HealthChecker.cs:42,73,85,115,163,198`、`HousekeepingService.cs:21,39,87,156,197,283`） | 规则与历史混在正文，阅读成本高 | P5 | OPEN |
| W-20 | WPF-T1.1 | 启动期在 UI 线程同步等建库：`EnsureCreatedAsync().GetAwaiter().GetResult()`（`App.xaml.cs:117`） | 冷启动卡顿；异常路径有死锁风险 | P1 | OPEN |
| W-21 | WPF-L1.4 | 关闭没有应用级取消令牌与有序停止：`App.OnExit` 直接 Dispose 容器（`App.xaml.cs:235-250`），`MainViewModel.Dispose` 只停触发（`MainViewModel.cs:315-319`） | 在途帧可能访问已释放服务；关窗退出时间不可控 | P2 | OPEN |
| W-22 | 具体缺陷 | 旧实现 `ControlPanelViewModel.cs:134` 判断 `_camera is FileCamera`，但 DI 注入的是 `SwappableCamera`（`ServiceCollectionExtensions.cs:67`）；已改为 `_camera.open()`（`ControlPanelViewModel.cs:139`） | File/Hik 重连已恢复；Webcam 的 `open()` 在 `_opened` 时仍是 no-op，另记 | P1 | FIXED |
| W-23 | 具体缺陷 | 旧实现 `RoiEditorViewModel.cs:286-301` 先改内存再 `File.WriteAllText`，并直接弹 `MessageBox`；现改为 copy-on-write + `IRecipeStore`（`RoiEditorViewModel.cs:291-321`） | 写失败不再污染内存配方；临时文件原子替换 | P1 | FIXED |
| W-24 | WPF-V1.2 | View 只有带参构造函数，无法设计时实例化，也没有 `d:DataContext`（如 `ControlPanelView.xaml.cs:7-11`、`CameraPageView.xaml.cs:7-11`） | XAML 设计器空白；View 只能由 DI 创建，进一步锁死 W-03 | P2 | OPEN |

---

## 4. 迁移路线

| 阶段 | 内容 | 主要文件 | 状态 |
|---|---|---|---|
| **P0** | 规则、现状、ADR、AGENTS 索引 | `docs/`、`AGENTS.md` | **DONE**（2026-10-20） |
| **P1** | 护栏：`VisionStation.App.Tests`；`IUiDispatcher` / `IDialogService` / `INavigationService`；去 VM 内 `MessageBox`；修 W-12/W-20/W-22/W-23 | App、新测试工程 | IN_PROGRESS（S1-1~S1-6 DONE；W-20 未做；S1-5 缩放/平移 NOT_RUN） |
| **P2** | 组合与生命周期：View 退出 DI；DataTemplate 导航；显式启动/停止；单例审计；修 W-01/W-02/W-03/W-04/W-05/W-21/W-24 | App、Devices | IN_PROGRESS（S2.1 DONE；W-20/W-21/W-01 等仍 OPEN） |
| **P3** | 线程与工作流：检测/存图移出 UI 线程；`Freeze`；日志 sink；结果面板数据驱动；修 W-06/W-07/W-08/W-09/W-11/W-13 | App、Service | NOT_RUN |
| **P4** | 配置与路径：强类型 Options；安装资源/用户数据分离；`reloadOnChange` 决策；修 W-14/W-15 | App、Service | NOT_RUN（需决策） |
| **P5** | 资源、命名、注释：拆 ResourceDictionary；`Open/Close`；事故编号移出业务代码；修 W-16/W-18/W-19 | App、Devices、docs | NOT_RUN |

> 执行切片、验收、回滚与停止线见 `docs/WPF改造指南.md`；本表只保留阶段状态。

**顺序约束**：

- P1 必须先于 P2/P3：没有 App 层测试，改生命周期和线程只能靠肉眼回归。
- P2 必须先于 P3：先确定对象由谁创建/释放，再决定哪些工作放到后台。
- P4 的路径方案涉及现场部署，必须先确认再动代码。
- P5 可以穿插，但不要在 P2/P3 同时改命名和结构，否则 diff 无法审查。

---

## 5. 待决策项

以下问题不能在实现时自行拍板：

1. **导航选型**：DataTemplate + `INavigationService`（推荐）还是保留少量 code-behind 切换页面。
2. **App 测试工程**：是否新增 `tests/VisionStation.App.Tests`（`net8.0-windows`，引用 App）；
   现有 `VisionStation.UnitTests` 保持 `net8.0` 不动。
3. **可写数据路径**：是否从 cwd 改为 `%LocalAppData%\VisionStation`；若现场需要便携模式，
   例外如何声明（配置项/启动参数/ADR）。
4. **是否引入 `IHost` / `HostedService`**：推荐不引入，用显式 `StartAsync/StopAsync`；
   若引入，必须证明关闭顺序和取消语义比显式方案更简单。
5. **`Trigger:Mode` 的语义**：`appsettings.json:9` 当前未被读取。是删除，还是接通
   Manual/Timer 的选择逻辑？这属于行为决策，不能顺手改。

---

## 6. 已知的文档漂移（非本次引入）

- `AGENTS.md` 第 6 节引用 `docs/三步失效分析法.md` 与 `docs/三步法落地记录.md`，但这两份文件
  在当前工作区已被删除（`git status` 显示 `D`）。P0 只在索引中标注该状态，不擅自恢复内容。
- `README.md:10` 仍写 `VisionStation.App/` 旧目录结构（实际在 `src/` 下），属于已有漂移，
  本次不改。

---

## 7. 验证记录

### P0（2026-10-20）

- 变更类型：仅文档 + 索引，无业务代码、无测试断言、无审计产物改动。
- 新增：
  - `docs/WPF开发规范.md`
  - `docs/WPF规范落地记录.md`
  - `docs/ADR-004-WPF开发规范与迁移路线.md`
- 修改：`AGENTS.md`（docs 索引、WPF 硬规则、做完的定义、测试数量 100 → 192）。
- 未触碰：`src/`、`tests/`、`audit/`。工作区里原有的 `docs/` 删除与根目录未跟踪文件
  （`dl_list.txt`、`s1.json`、`t1.json`）不是本次改动。

**`verify.cmd` 实测（2026-10-20）**

```text
== 1/3 build ==
已成功生成。0 个警告，0 个错误。
== 2/3 test ==
已通过! - 失败: 0，通过: 192，已跳过: 0，总计: 192
== 3/3 gate ==
结果: 0 错误 / 0 警告
VERIFY OK
```

- 退出码：0（`VERIFY OK`）。
- 测试数量：192（AGENTS.md 原写 100 为过期数据，本次同步更正）。
- `git diff --stat`：已跟踪文件仅 `AGENTS.md`（+22 / -3）；另外新增 3 个未跟踪文档
  （`WPF开发规范.md`、`WPF规范落地记录.md`、`ADR-004-WPF开发规范与迁移路线.md`）。
  工作区原有的 `docs/` 删除（10 个文件）不是本次改动，未恢复、未覆盖。
- UI 实际走查：**NOT_RUN**。P0 没有 UI/业务代码改动；按 AGENTS.md 第 7 节第 6 条，
  P1 起涉及 WPF 代码的切片必须补真实进程走查记录。
- 未执行项：`VisionStation.App.Tests` 尚不存在，属于 P1；本次不声称 App 层有测试覆盖。

> 注：`docs/ADR-004-WPF开发规范与迁移路线.md` 已在 2026-10-20 的文档清理中删除；
> 本节保留 P0 当时的历史记录，不再作为有效索引。

### S1-1（2026-10-20）：App.Tests 骨架 + CI 接线

- 变更类型：新增测试工程与 CI 接线，无业务代码、无现有测试断言改动。
- 新增：
  - `tests/VisionStation.App.Tests/VisionStation.App.Tests.csproj`
  - `tests/VisionStation.App.Tests/ImagePanelViewModelTests.cs`（2 个测试）
- 修改：
  - `ci.cmd`：构建并运行 App.Tests；App.Tests 使用 `--no-dependencies`，
    避免重复构建 App 产生 `_wpftmp` 重复告警。
  - `Directory.Build.props`：App.Tests 不进 Sonar。
  - `VisionStation.slnx`：加入 App.Tests。
  - `AGENTS.md`：测试数 192 → 194，索引加入 App.Tests。
- `ci.cmd` 实测：

```text
CI OK
UnitTests：192/192
App.Tests：2/2
告警基线：129，持平
```

- W-17 仍为 `OPEN`：只建立了骨架和 2 个 ImagePanel 测试；
  ControlPanel / RoiEditor 的 VM 测试在 S1-3 / S1-4。
- UI 实际走查：**NOT_RUN**（本步没有 UI 行为改动）。

### S1-2（2026-10-20）：IRecipeStore + JsonRecipeStore

- 变更类型：新增 Service 层配方保存端口与实现，无 VM/UI 行为改动。
- 新增：
  - `src/VisionStation.Service/Recipes/IRecipeStore.cs`
  - `src/VisionStation.Service/Recipes/JsonRecipeStore.cs`
  - `tests/VisionStation.UnitTests/JsonRecipeStoreTests.cs`（3 个测试）
- 行为：
  - 保存走唯一临时文件，完整序列化后 `File.Move(overwrite)`，失败清理临时文件；
  - JSON 使用 camelCase，与 `Config/recipes/default.json` 一致；
  - `SaveAsync` 返回最终绝对路径。
- 修改：
  - `ServiceCollectionExtensions.AddAppServices` 注册 `IRecipeStore`。
  - `AGENTS.md` 测试数 194 → 197。
- `ci.cmd` 实测：

```text
CI OK
UnitTests：195/195
App.Tests：2/2
告警基线：129，持平
```

- W-23 仍为 `OPEN`：保存器已就绪，`RoiEditorViewModel` 接入在 S1-4。
- UI 实际走查：**NOT_RUN**（本步没有 UI 行为改动）。

### S1-3（2026-10-20）：IDialogService + 控制面板（DONE）

- 变更类型：建立对话框端口，迁移 ControlPanel 的急停确认；修 W-22 重连缺陷。
- 新增：
  - `src/VisionStation.App/Abstractions/IDialogService.cs`
  - `src/VisionStation.App/Infrastructure/WpfDialogService.cs`
  - `tests/VisionStation.App.Tests/ControlPanelViewModelTests.cs`（3 个测试）
- 修改：
  - `ControlPanelViewModel` 注入 `IDialogService`；`EmergencyStopAsync` 取消不 `Stop()`、确认只 `Stop()` 一次。
  - `Reconnect` 删除 `_camera is FileCamera`，改为 `_camera.open()`（`ControlPanelViewModel.cs:139`）。
  - `ServiceCollectionExtensions.AddViews` 注册 `IDialogService`。
  - `AGENTS.md` 测试数 197 → 200。
- 测试：
  - 急停取消 → `Stop` 未调用；
  - 急停确认 → `Stop` 调用一次；
  - 重连 → `open()` 调用一次、`CameraOnline` 跟随 `IsAvailable`。
- `ci.cmd` 实测：

```text
CI OK
UnitTests：195/195
App.Tests：5/5
告警基线：129，持平
```

- W-22 状态改为 `FIXED`（File/Hik 路径）。
- W-10 仍为 `OPEN`：ControlPanel 已迁移，`RoiEditorViewModel` 保存提示在 S1-4 关闭。
- 已知边界：`WebcamCamera.open()` 在 `_opened` 且连续读帧失败后仍是 no-op，本切片不扩大。
- UI 实际走查：**PASS**
  - 启动：临时 File 模式副本，自检 5 项 OK。
  - ControlPanel 重连：触发后日志出现 `[FileCamera] 刷新完成`。
  - 相机页重连：同样出现 FileCamera 刷新。
  - 急停取消：对话框返回 `No`，未继续执行急停（VM 测试覆盖 `Stop` 未调用）。
  - 急停确认：对话框返回 `Yes`，`AutoButtonText` 从“自动运行中”回到“自动模式”。
- 临时诊断日志已删除；上表 `ci.cmd` 为删除后重跑结果。

### S1-4（2026-10-20）：ROI 保存（DONE）

- 变更类型：把 RoiEditor 的直接文件 IO/JSON/MessageBox 迁到 `IRecipeStore + IDialogService`。
- 修改：
  - `RoiEditorViewModel` 构造函数改为 `(CoordinateMapper, RecipeConfig, IRecipeStore, IDialogService)`；
  - 删除 `AppPaths`、`System.IO`、`System.Text.Json`、`MessageBox`；
  - `SaveToFileAsync`（`RoiEditorViewModel.cs:291`）先生成新 ROI 列表并复制 `RecipeConfig`，
    保存成功后才更新 `_recipe.Rois`、发 `RoisUpdatedMessage`、显示成功；
  - 保存失败只显示错误，`_recipe.Rois` 保持旧值，异常不逃逸。
- 新增测试：`tests/VisionStation.App.Tests/RoiEditorViewModelTests.cs`（2 个）
  - 保存成功 → store 收到新 ROI、内存配方更新、成功提示；
  - 保存失败 → 内存配方不变、错误提示、无异常逃逸。
- `ci.cmd` 实测：

```text
CI OK
UnitTests：195/195
App.Tests：7/7
告警基线：127（129 -> 127，CA1869 随 RoiEditor 旧实现删除）
```

- W-23 / W-10 状态改为 `FIXED`（ViewModels 下已无 `MessageBox`）。
- UI 实际走查：**PASS**
  - 成功：临时 File 模式副本进入 ROI 编辑，点“保存”后出现“保存成功”，
    `Config/recipes/default.json` 从 1163B 变为 1481B，JSON 以 camelCase 开头。
  - 失败：把目标路径替换成目录后再次保存，出现“保存失败”，未出现成功框。
  - 重启后的 ROI 回读不在 S1-4 验收范围，未作为 PASS 条件。

### S1-5（2026-10-20）：图像双写（W-12，DONE；缩放/平移 NOT_RUN）

- 变更类型：删除 View 对 `Image.Source` 的第二次赋值，保留 XAML 绑定作为唯一真相源。
- 修改：
  - 删除 `ImagePanelView.xaml.cs` 的 `PropertyChanged` 订阅；
  - 删除 `ImageDisplay.Source = _vm.CurrentImage`；
  - 保留 `ImagePanelView.xaml:86` 的 `Source="{Binding CurrentImage}"`；
  - 保留 `_vm`、缩放/平移/鼠标坐标逻辑。
- `ci.cmd` 实测：

```text
CI OK
UnitTests：195/195
App.Tests：7/7
告警基线：127，持平
```

- UI 实测：临时 File 模式副本中切“原图/检测”，图像区域截图 SHA256 不同
  （`5236...` vs `B71F...`）；切回原图后恢复为 `5236...`，证明绑定在双向切换时都生效。
- 缩放/平移的鼠标输入自动化未送达（`CoordLabel` 未变化），记 `NOT_RUN`，不写 PASS。
- W-12 状态改为 `FIXED`：双写已删除且切 Tab 已实测；缩放/平移回归仍待人工补查。

### S1-6（2026-10-20）：S1 收口

- `ci.cmd`：`CI OK`；UnitTests 195/195；App.Tests 7/7；总数 202；告警 127。
- 静态检查：`ViewModels` 无 `MessageBox` / `File.WriteAllText`；
  `ImagePanelView.xaml.cs` 无 `PropertyChanged` / `ImageDisplay.Source`；
  `RoiEditorViewModel` 无 `AppPaths` / `System.IO` / `System.Text.Json`。
- UI 走查汇总：
  - 急停取消/确认：PASS；
  - ControlPanel 重连、相机页重连：PASS；
  - ROI 保存成功/失败：PASS；
  - 图像页切 Tab：PASS；
  - 图像页缩放/平移：`NOT_RUN`。
- W-10 / W-12 / W-17 / W-22 / W-23：`FIXED`。
- `AGENTS.md` 测试数：202；`artifacts/ci/warn-baseline.json`：127。
- 现有测试断言未改动：本阶段只新增测试文件。
- 残项：S1-5 缩放/平移未验证；S1-6 收口完成，但不把该项写成 PASS。

### S2.1（2026-10-20）：引入 `AppHost`（DONE）

- 新增 `src/VisionStation.App/Composition/AppHost.cs`、`IAppWindow.cs`；
  `MainWindow` 实现 `IAppWindow`，`App` 只保留异常处理、启动委托和健康检查展示。
- `AppHost` 拆出 `Configure` / `InitializeAsync` / `Show` / `StopAsync`；
  `InitializeAsync` 顺序仍为自检 → 管家 → 建库，`Show` 通过窗口工厂创建并显示一次窗口。
- `StopAsync` 幂等，释放 DI 容器并 flush 日志；尚未接入 `MainWindow.Closing`，W-21 仍 OPEN。
- 本次是行为不变抽取：`OnStartup` 仍 `.GetAwaiter().GetResult()`（W-20 仍 OPEN）；
  健康检查仍弹模态框；`DeviceManager` 仍在 DI 工厂 `ConnectAll`（S2.4 未做）。
- 测试接缝：`AppHost` internal 构造 + `InternalsVisibleTo VisionStation.App.Tests`；
  新增 5 个 `AppHostTests`，覆盖 Configure 不解析启动服务、Initialize 不显示窗口、
  建库失败不显示窗口、Show 只显示一次、StopAsync 幂等。
- 测试修复：`StopAsync_CalledTwice_DisposesServicesOnce` 首次失败（容器不释放外部实例），
  改为工厂注册后通过；没有改动既有测试断言。
- `ci.cmd`：`CI OK`；UnitTests 195/195；App.Tests 12/12；总数 207；告警 127 → 119。
- UI 走查：临时副本 + `Camera:Mode=File`，启动显示主窗口；关窗退出码 0；再次启动/关窗退出码 0；
  日志自检 `2/2` 通过，数据库/存储/模板/配方均 OK，无残留进程。
- 文档/基线：`AGENTS.md` 测试数改 207；`artifacts/ci/warn-baseline.json` 降至 119。
- 下一步：S2.2，去同步等待（W-20）、去启动模态框、启动失败非 0 退出。
