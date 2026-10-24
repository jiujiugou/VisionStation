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
| W-01 | WPF-L1.2 | 构造函数产生副作用：`HikCamera` 构造即连接（`src/VisionStation.Devices/Camera/HikCamera.cs:71-75`）；`DeviceManager` 在 DI 工厂里 `ConnectAll`（`src/VisionStation.App/Services/ServiceCollectionExtensions.cs:69-76`）；`MainViewModel` 构造内订阅触发并发消息（`src/VisionStation.App/ViewModels/MainViewModel.cs:97-117`）；多个 VM 构造内注册 Messenger（`LogPanelViewModel.cs:37-48`、`ResultPanelViewModel.cs:41-84`）。**设备部分已修（S2.4/S2.5）**：三个相机构造函数都只赋值，连接统一走 `open()`；`DeviceManager` 的 DI 工厂只 `Register`，`AppHost.InitializeAsync` 在自检前显式 `ConnectAll()` 并记录失败清单（`AppHost.cs:129-142`）；`SwappableCamera.SwapTo` 创建候选后显式 `open()` 再判可用（`SwappableCamera.cs:74-96`） | 启动顺序由“谁先解析”决定；VM 无法脱离 UI 测试 | P2 | FIXED（设备部分，见 W-01-retry） |
| W-01-retry | WPF-L1.2 | **口径已澄清（S5）**：页面 VM 全部 `AddSingleton`（活到进程结束），构造内订阅触发/Messenger 与对象生命周期一致，不存在"页面销毁后仍在收消息"；触发订阅现在由 `InspectionPageViewModel` 承担，且它在无 UI 线程的测试里可构造（`InspectionPageOrchestrationTests` 4 条）。若将来页面改 Transient，必须改成显式 `Initialize`/`Dispose`，不能只改注册 | 当前无泄漏；约束已写进指南 §1.1 | P2 | FIXED（口径澄清，无代码改动） |
| W-02 | WPF-L1.2 / L1.3 | 相机启动被初始化两次：`SwappableCamera` 构造时 `HikCamera` 已 `Initialize`（`ServiceCollectionExtensions.cs:57-63`、`HikCamera.cs:71-75`），随后 `ConnectAll` 的 `open()` 又 `TeardownDevice + Initialize`（`HikCamera.cs:387-394`）。**已修（S2.5）**：`HikCamera` 构造函数不再 `Initialize()`，只有 `open()` 会走完整七步；启动时唯一的一次连接来自 `AppHost.InitializeAsync` → `ConnectAll` | 启动变慢；设备被关闭/重开的失败窗口翻倍 | P2 | FIXED |
| W-03 | WPF-D1.2 / V1.1 | **已修（S3b）**：容器不再注册任何 View / `MainWindow`（`ServiceCollectionExtensions.cs:162-184` 只剩 VM 与服务），视图由 `App.xaml` 的 9 条 `DataTemplate` 创建（`App.xaml:14-56`）；`MainWindow` 构造函数从 12 个参数降到 1 个（`MainWindow.xaml.cs:25`），且由 `AppHost` 直接 `new`（`AppHost.cs:68`） | View 生命周期归可视树，容器只管 VM；`AppCompositionTests` 是回退报警器 | P2 | FIXED |
| W-04 | WPF-N1.1 / A1.5 | **已修（S3b）**：`INavigationService` 持有路由表与当前页（`NavigationService.cs:10`、`Navigate` 在 `:62`），未知路由返回 false 并留 WRN；`SidebarViewModel` 改为「选中项 → 导航」（`SidebarViewModel.cs:70`），点未实现的页签把选中项退回去；`NavigateMessage` 已删除（`Messages.cs` 只留一句说明）；`MainWindow.xaml:29` 绑 `CurrentPage` | 导航是状态不是通知；未知路由不会静默换空页；`NavigationServiceTests` 4 条 | P2 | FIXED |
| W-05 | WPF-V1.3 | **已修（S3a/S3b）**：`MainWindow.xaml.cs` 只剩 `InitializeComponent` + `DataContext` + `Closing` 转发；导航搬进 `NavigationService`，F2 搬成 `Window.InputBindings` → `ShellViewModel.ToggleRoiEditorCommand`（`ShellViewModel.cs:66`，按当前页类型路由，非检测页不做事），编辑态搬进 `InspectionPageViewModel`（`:54`）；动态窗口标题取消（纯装饰，且属于 View） | code-behind 不再承载业务；快捷键跟窗口走，不依赖焦点落点 | P2 | FIXED |
| W-06 | WPF-A1.2 / A1.3 | **已修（S4a）**：`MainViewModel` 已删除（-342 行），链路搬进 `InspectionWorkflow.RunOnceAsync`（`Services/Inspection/InspectionWorkflow.cs:85`）；统计改由 `StatusBarViewModel` 自己记账，消息由页面 VM 发一次；编排只剩触发入口、防重入、广播三件事（`InspectionPageViewModel.cs:157`） | 工作流不再依赖界面；VM 只做编排 | P3 | FIXED |
| W-07 | WPF-T1.1 / T1.2 | **已修（S4b）**：`RunOnceAsync` 整段 `Task.Run`（`InspectionWorkflow.cs:80`）；触发入口用构造注入的 `SynchronizationContext` post 一次到 UI（`InspectionPageViewModel.cs:135`），`await` 后续体回 UI 线程再广播。**VM 内无 `Dispatcher` / `Application.Current`** | 检测/ONNX 推理/存库不再占用 UI 线程；消息广播仍保证在 UI 线程 | P3 | FIXED |
| W-08 | WPF-T1.3 | **已修（S4a）**：`MatBitmap.ToBitmapSource` 创建后立即 `Freeze()`（`Services/Imaging/MatBitmap.cs:62`），空图返回 null；`MatBitmapTests` 5 条钉住冻结、像素、四通道、空图、入参所有权 | 位图可跨线程绑定 | P3 | FIXED |
| W-09 | WPF-T1.5 | **已修（S4e）**：日志入 `ConcurrentQueue`，由 UI 线程一次 `BeginInvoke` 合批灌入，每批只重建一次可见列表（`LogPanelViewModel.cs:74-135`）；`ICollectionView` 已移除（它有 Dispatcher 亲和性，跨线程改源集合抛 `NotSupportedException`，是实测撞到的）；关窗前 `FlushPending()`（`AppHost.cs:277`） | 后台发日志不再等 UI；面板不再逐条刷新 | P3 | FIXED |
| W-10 | WPF-E1.2 / V1.3 | `ControlPanel` 已迁到 `IDialogService`（`ControlPanelViewModel.cs:119`）；`RoiEditorViewModel` 保存已改为 `IRecipeStore + IDialogService`（`RoiEditorViewModel.cs:291-321`） | ViewModels 下已无 `MessageBox`；保存成功/失败可测 | P1 | FIXED |
| W-11 | WPF-A1.2 / B1.4 | **已修（S4d）**：遍历 `report.Results` 出行，值用 `ToolResult.Summary`，未知工具显示原名（`ResultPanelViewModel.cs:72-86`）；VM 只输出 `IsOk`，颜色交给 XAML `DataTrigger`（`ResultPanelView.xaml:52-95`）；VM 内零十六进制 | 工具增减不会再让面板错行；主题可统一切换 | P3 | FIXED |
| W-12 | WPF-V1.4 / B1.1 | 旧实现同时有 XAML 绑定 `Source="{Binding CurrentImage}"`（`ImagePanelView.xaml:86`）与 code-behind 手动赋值（`ImagePanelView.xaml.cs:25-29`）；现已删除手动写入 | 同一属性双写已消除；切 Tab 哈希验证通过 | P1 | FIXED |
| W-13 | WPF-E1.1 | 日志双轨**仍未合并**：Serilog 写控制台/文件，UI 面板另靠 `LogMessage`（`Messages.cs:39-47`、`LogPanelViewModel.cs:37-48`）。S4e 只解决了「怎么刷」（合批、非阻塞），没解决「记两遍」（`UiLogSink` 属独立一片） | 系统日志仍进不了 UI 面板；两套记录对不上 | P3 | OPEN（S4 明确不做） |
| W-14 | WPF-C1.1 / C1.3 | **已修（S5）**：绑定与校验收口到 `AppOptionsBinder.Bind`（`AppOptions.cs:59`），`Camera:Mode` 只经 `CameraOptions` 消费；`AppPaths` / `CameraFactory` / `ServiceCollectionExtensions` 不再依赖 `IConfiguration`；`reloadOnChange` 关闭（无消费者）；`Trigger:Mode` 删除（死键，触发方式实际由界面按钮决定）。数字/枚举非法时报错**带键名与合法值**，聚合后非 0 退出 | 默认值与解析只有一处；配错启动期就炸，不会静默漂移 | P4 | FIXED |
| W-15 | WPF-C1.2 | **已修（S5，便携模式拍板）**：可写数据根 = `--data-root` 启动参数 &gt; exe 目录（`AppOptions.cs:59`、`App.xaml.cs:36` 解析参数）；`AppPaths` 的可写路径只认数据根，不再使用 `Environment.CurrentDirectory`（`AppPaths.cs:68`），读资源 exe 目录优先、cwd 兜底；旧数据**不迁移**（拍板：全部删除，从空库开始）。`AppPathsTests` 6 条钉住 cwd 解耦 | 快捷方式/计划任务启动不再丢数据；整个发布目录可整体搬迁 | P4 | FIXED |
| W-16 | WPF-R1.1 / R1.2 / R1.3 | **已修（S6）**：资源拆成 `Resources/Theme.xaml`（配色）、`Converters.xaml`（转换器）、`Controls.xaml`（样式，自带 Theme 合并）；`App.xaml` 只剩合并 + 9 条 DataTemplate。视图内十六进制色值 **52 → 0**，全部改用命名资源；三个视图自带的重复转换器注册已删除（`ControlPanelView`/`LogPanelView`/`StatusBarView`），全应用只在 `Converters.xaml` 注册一次。顺带删除无人使用的 `StringToColorConverter` | 换主题只改 Theme.xaml；资源键不再有遮蔽 | P5 | FIXED |
| W-17 | WPF-TS1.1 / A1.6 | 旧：App 层零测试，测试工程 `net8.0` 且未引用 App；现有 `tests/VisionStation.App.Tests`（7 个测试），UnitTests 195 + App.Tests 7 = 202 | 关键 VM 命令已有护栏；生命周期/导航测试仍待 S2 扩展 | P1 | FIXED |
| W-18 | WPF-S1.1 | **已修（S6）**：`ICamera.open/close` → `Open/Close`（`ICamera.cs:32,35`）；四个实现、`SwappableCamera`、`CameraDevice`、`ControlPanelViewModel`、控制台程序与全部测试同步（11 个文件）。残留小写引用 0 | 契约与其余 PascalCase API 一致 | P5 | FIXED |
| W-19 | WPF-S1.3 | **已修（S6）**：20 处 `修复（F-xxx）` 全部移入新建的 `docs/代码事故索引.md`（编号 → 现象 → 现在的做法），代码注释只留"为什么"；`R-003` 属 ADR 编号（架构决议）保留并在索引中说明两套编号的区别 | 规则与历史分离，代码注释不再需要读编号 | P5 | FIXED |
| W-20 | WPF-T1.1 | 启动期同步等建库已消除：`OnStartup` 同步入口 + `_ = StartAsync()` fire-and-forget（`App.xaml.cs:27,57`），`await host.InitializeAsync(...)`（`App.xaml.cs:74`）由 `AppHost.InitializeAsync` 内 `await EnsureCreatedAsync`（`AppHost.cs:118`）；健康检查弹框改为状态栏/日志面板上报（`App.xaml.cs:97`） | 冷启动不再卡 Dispatcher；启动失败走 `Shutdown(1)` | P1 | FIXED |
| W-21 | WPF-L1.4 | 关闭已有序：`IAppWindow.Closing` → `AppHost.HandleWindowClosingAsync`（`AppHost.cs:180`）→ `AppHost.StopAsync`（`AppHost.cs:212`）六步**直线代码**，每步一行带编号日志；`ShutdownState`（`ShutdownState.cs:13`，bool + CTS）提供应用级取消，`MainViewModel.OnTriggered` 先查 `IsStopping` 再碰 Dispatcher（`MainViewModel.cs:141`）；`ProcessFrame` 用 `InFlightWorkMonitor` 租约登记在途（`MainViewModel.cs:169`）；`App.OnExit` 降级为有界兜底（`App.xaml.cs:191`） | 在途帧不会落在已释放的容器上；关窗有上界（8s 预算，实测 279-375ms） | P2 | FIXED |
| W-22 | 具体缺陷 | 旧实现 `ControlPanelViewModel.cs:134` 判断 `_camera is FileCamera`，但 DI 注入的是 `SwappableCamera`（`ServiceCollectionExtensions.cs:67`）；已改为 `_camera.open()`（`ControlPanelViewModel.cs:139`） | File/Hik 重连已恢复；Webcam 的 `open()` 在 `_opened` 时仍是 no-op，另记 | P1 | FIXED |
| W-23 | 具体缺陷 | 旧实现 `RoiEditorViewModel.cs:286-301` 先改内存再 `File.WriteAllText`，并直接弹 `MessageBox`；现改为 copy-on-write + `IRecipeStore`（`RoiEditorViewModel.cs:291-321`） | 写失败不再污染内存配方；临时文件原子替换 | P1 | FIXED |
| W-24 | WPF-V1.2 | **已修（S3b）**：9 个 View 全部无参构造；每个 View 都加了 `d:DataContext="{d:DesignInstance vm:XxxViewModel, IsDesignTimeCreatable=False}"` + `mc:Ignorable="d"`（如 `CameraPageView.xaml:1-8`）；需要 VM 的 code-behind（`ImagePanelView` / `RoiEditorView`）改为从 `DataContext` 取 | 设计器可打开且不再依赖 DI；预览无数据（面板 VM 有依赖，`IsDesignTimeCreatable=False`） | P2 | FIXED |

---

## 4. 迁移路线

| 阶段 | 内容 | 主要文件 | 状态 |
|---|---|---|---|
| **P0** | 规则、现状、ADR、AGENTS 索引 | `docs/`、`AGENTS.md` | **DONE**（2026-10-20） |
| **P1** | 护栏：`VisionStation.App.Tests`；`IUiDispatcher` / `IDialogService` / `INavigationService`；去 VM 内 `MessageBox`；修 W-12/W-20/W-22/W-23 | App、新测试工程 | IN_PROGRESS（S1-1~S1-6 DONE；W-20 已 FIXED；`IUiDispatcher`/`INavigationService` 未做；S1-5 缩放/平移 NOT_RUN） |
| **P2** | 组合与生命周期：View 退出 DI；DataTemplate 导航；显式启动/停止；单例审计；修 W-01/W-02/W-03/W-04/W-05/W-21/W-24 | App、Devices | **DONE**（S2、S3 全部完成；W-01/W-02/W-03/W-04/W-05/W-20/W-21/W-24 FIXED；W-01 的 VM 订阅部分见 W-01-retry，属 S4） |
| **P3** | 线程与工作流：检测/存图移出 UI 线程；`Freeze`；日志 sink；结果面板数据驱动；修 W-06/W-07/W-08/W-09/W-11/W-13 | App、Service | **DONE**（S4；W-06~W-09/W-11 FIXED；W-13 拆分另做） |
| **P4** | 配置与路径：强类型 Options；安装资源/用户数据分离；`reloadOnChange` 决策；修 W-14/W-15 | App、Service | **DONE**（S5；W-14/W-15 FIXED） |
| **P5** | 资源、命名、注释：拆 ResourceDictionary；`Open/Close`；事故编号移出业务代码；修 W-16/W-18/W-19 | App、Devices、docs | **DONE**（S6；W-16/W-18/W-19 FIXED） |

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

### S2.2（2026-10-20）：`App.xaml.cs` 异步启动、去启动模态框（DONE）

- `OnStartup` 保持同步（`App.xaml.cs:27`），启动体移入 `private async Task StartAsync()`（`App.xaml.cs:60`），
  入口用 `_ = StartAsync()`（`App.xaml.cs:57`）显式 fire-and-forget —— `OnStartup` 签名是 `void`，重写不能是
  `async Task`，而 `async void` 的异常会掀翻进程，所以改成「同步入口 + Task 方法内全包 try/catch」。
  `await host.InitializeAsync(...)`（`App.xaml.cs:74`）替换 `.GetAwaiter().GetResult()`，W-20 FIXED。
- 健康检查不再弹 `MessageBox`：新增 `ReportHealthFailures`（`App.xaml.cs:97`），失败项写
  状态栏 `StatusText` 与日志面板；文件日志由 `AppHost.InitializeAsync` 逐项写 Error。
  新增纯函数 `HealthSummary.Describe`（`Composition/HealthSummary.cs`）生成状态栏文案。
- 启动失败仍 `LogCrash("startup", ex)` + `Shutdown(1)`（`App.xaml.cs:80-88`）；`OnExit` 未改，
  本来就没有 `.GetAwaiter().GetResult()`。
- 未改：`AppHost.LoadRecipe` 仍回退内嵌默认配方（配方损坏当前不致命，与 S2 验收第 1 条不符，见“待决策”）；
  `StopAsync` 仍未接 `MainWindow.Closing`（W-21 OPEN）；`DeviceManager` 仍在 DI 工厂 `ConnectAll`（S2.4）。
- 新增 3 个 `HealthSummaryTests`：无失败 / 单项 / 多项只列失败项且保序。
- `ci.cmd`：`CI OK`；UnitTests 195/195；App.Tests 15/15；总数 210；告警 119 持平。
- UI 走查（临时副本 + `Camera:Mode=File`，绝对路径指向临时目录）：
  - 正常启动：主窗口 1 个、退出码 0、关窗 184-322 ms；自检 5 项全 OK；DB `journal=wal`。
  - 模板路径缺失（制造 FAIL）：仍显示主窗口，无 `#32770` 模态窗口；
    状态栏文字 `自检 1 项异常: 模板文件`、日志面板 `自检失败 模板文件: 文件不存在`、文件日志 `[ERR] 自检 模板文件: FAIL`；退出码 0。
  - 启动失败：DB 路径被文件占位 → 不显示窗口，退出码 1，`crash.log` 与日志 `[FTL] 未处理异常 (startup)`
    记录 `SqliteException: SQLite Error 14: 'unable to open database file'`。
- 待决策：配方损坏是回退还是判为启动失败（当前回退）；自检失败项哪些致命/哪些降级。
- 下一步（当时）：S2.3 关闭顺序 —— 已完成，见下节。

### S2.3（2026-10-20）：关闭顺序（DONE，W-21 FIXED）

验收口径（行为契约，先定"必须做到什么"再写代码）：C1 预算内退出且码 0、C2 关闭后不再有新帧、C3 在途帧有界收尾且放弃要留痕、
C4 退出无残留、C5 顺序不可颠倒、C6 关闭幂等、C7 单步失败不阻断后续、C8 兜底路径有界退出、
C9 看门狗语义不变、C10 关闭不冻界面。每条契约的验证层与证据见本节末尾的对照表。

- `src/VisionStation.App/Composition/Lifecycle/` 只有两个类，都不带接口：`ShutdownState`（bool + CTS，幂等取消）、
  `InFlightWorkMonitor`（在途租约 + 归零唤醒 + 带超时等待）。关闭顺序**不拆编排器**，直接写在 `AppHost.StopAsync`
  的直线代码里，每步一行带编号日志。理由：WPF 只有一条关闭路径，"可注入的六步"只对 mock 有用；
  `docs/WPF开发规范.md:25` 明确反对 Host 管启停那套。
- 接缝：`IAppWindow` 加 `Closing` 事件（`WindowCloseRequestedEventArgs`，不依赖 WPF 的 `CancelEventArgs`）；
  `MainWindow` 显式实现接口并把 WPF `Closing` 转发过去（`MainWindow.xaml.cs:73`）；`AppHost.Show` 订阅、
  `HandleWindowClosingAsync` 接管（首次 Cancel 留住窗口、重复请求只 Cancel 不重复收尾）；
  `StopAsync` 顺序：停触发 → 应用级取消 → 等在途（有上界）→ 后台线程释放容器 → 刷日志 → 通知看门狗；
  任一步失败只留痕、不阻断后续；`Configure` 未完成时没有可收尾对象，直接返回；
  `App.OnExit` 降级为有界兜底（`Wait(2s)`，不无限等）。
- 应用级取消与在途计数：`MainViewModel.OnTriggered` 第一行查 `IsStopping`（必须先于 `Application.Current.Dispatcher`）；
  `ProcessFrame` 用 `using var lease = _inFlight.Begin()` 登记，所有退出路径（含中途 return/异常）都归还。
- 实现时踩到两个顺序坑，均已修并写进注释：
  1. `AddSerilog(dispose: true)` 会让容器释放（第 4 步）提前拆掉日志 sink —— 实测日志只到第 2 步，
     关闭摘要全部丢失。改成 `dispose: false`，日志生命周期统一由第 5 步控制；摘要必须写在 flush **之前**。
  2. `wait-idle` 成功时不写日志，等于「没看到就当好」的恒真判据。改成成功/超时各记一条（`ok` / `timeout`）。
- 测试：App.Tests 23（新增 8：`ShutdownStateTests` 1 + `InFlightWorkMonitorTests` 4 + `MainViewModelShutdownTests` 1 +
  `AppHostTests` 2）、UnitTests 195，总 **218**；告警 119 → 115（顺手清掉 S2696×3、S6966×2，基线自动下调）。
- 走查（临时副本 + `Camera:Mode=File`，绝对路径指向临时目录；收敛后复测）：
  - 正常启动 6.8s → 关窗 **308ms**，退出码 0；日志五步带编号：
    `1 stop-trigger（0ms）` → `2 cancel（3ms）` → `3 在途收尾: ok（3ms）` → `4 释放容器（6ms）` → `5 刷日志（总 7ms）`；
    第 6 步不写日志（flush 之后 sink 已关），由"关窗后 10s 无新进程"证明。
  - 二次启动 3.2s → 关窗 328ms，退出码 0；`Get-Process VisionStation.App` 计数 0（无残留）。
  - 连点关闭 3 次（间隔 40ms）：每次运行只产生 1 份摘要；两次运行合计 `[关闭]` 16 行（每次 8 行）。
  - 日志 grep `disposed` / `ObjectDisposed`：**0 命中**。
  - 启动失败（`Storage:DbPath` 指向「文件下的子路径」）：退出码 **1**、无窗口。
  - 关窗后观察 10s：无新 `VisionStation.App` 进程，`VisionStation.Watchdog` 计数 0（收到 clean 信号，未重启）。
- `NOT_RUN`（缺什么写清楚，不写成 PASS）：
  - 「自动运行中关窗」：需要点「自动模式」按钮，本机 UI 自动化注入被策略拦截（`Add-Type` 被禁用），
    未取得 L3 证据。L1 用**真实** `InFlightWorkMonitor` 的用例 `最后一租约释放后才完成`
    （两个租约，释放一个后 `wait.IsCompleted == false`，第二个释放才放行）提供等价证据。
  - 「正逢清理轮次关窗」：清理轮次为毫秒级，无法稳定制造 5s 级阻塞；C10 以关窗总耗时（< 350ms）为代理证据，
    释放步骤固定走 `Task.Run`（`AppHost.StopAsync` 第 4 步），由代码审阅 + `4 释放容器（6ms）` 日志佐证。
  - Hik 真机句柄释放：无真机，仅验 File 模式。
- 待决策：关闭总预算 8s 是否合适（备选严格 5s）；超时后看门狗是否仍算 clean（当前：发 clean + 记 Warning）。
- 契约 → 证据对照：

| 契约 | 结果 | 证据 |
|---|---|---|
| C1 预算内退出、退出码 0 | PASS | 关窗 279 / 328 / 375 ms，退出码均 0 |
| C2 关闭后不再有新帧 | L1 PASS / L3 `NOT_RUN` | `MainViewModelShutdownTests`：`BeginStop()` 后 `Fire()` 采图 0 次 |
| C3 在途有界收尾、放弃留痕 | L1 PASS / L3 部分 | 正向「最后租约释放才放行」+ 反向「超时不阻塞线程」；日志 `3 在途收尾: ok（3ms）` |
| C4 退出无残留 | PASS | 进程计数 0；二次启动 3.2s、无「设备被占用」 |
| C5 顺序不可颠倒 | PASS | 日志五行带序号；grep `disposed`/`ObjectDisposed` 0 命中 |
| C6 关闭幂等 | PASS | `ShutdownState.BeginStop` 重复调用；连点 3 次 X 每次运行只 1 份关闭日志 |
| C7 单步失败不阻断后续 | PASS（L1） | 停触发抛异常后 5 步仍执行，不向调用方抛 |
| C8 兜底路径有界退出 | PASS | 启动失败退出码 1、无窗口 |
| C9 看门狗语义不变 | PASS | 关窗后 10s：App 0、Watchdog 0 |
| C10 关闭不冻界面 | PASS（代理） | 关窗总耗时 < 350ms；释放步骤固定 `Task.Run` |

- 设计收敛（同日纠偏）：首版照 Generic Host 思路拆成 12 个类型（`IAppLifetime`≈`IHostApplicationLifetime`、
  `IShutdownStages`+`AppShutdownStages` 为 mock 而存在的三层、`IShutdownCoordinator`、`ILogShutdown`、`IWatchdogSignal`…），
  被指出"这是用 ASP.NET Core 的方式写 WPF"。已砍到 2 个类，净减约 600 行、10 个类型。
  代价如实记录：六步顺序从单测降级为「带编号日志 + 一次真实走查」，不再有 `ShutdownCoordinatorTests`。
- W-21 → `FIXED`。W-01/W-02 已在 S2.4/S2.5 关闭（设备部分）。

### S2.4（2026-10-20）：设备连接移出 DI 工厂（DONE，W-01 设备部分 / W-02 FIXED）

改了三处：

1. `DeviceManager.ConnectAll()` 由 `void` 改为返回失败清单。失败判据不止异常——各实现的
   `Connect` 内部基本都自己 catch（相机打不开是常态，不该把进程带走），所以 `Connect` 之后
   还要查一次 `IsConnected`，否则"调了但没连上"会被当成成功。
2. `ServiceCollectionExtensions.AddDevices` 的工厂只 `Register`，不再 `ConnectAll`。
   解析一个对象不该有"打开设备"这种副作用；否则启动顺序由"谁先被解析"决定，
   测试一建容器就会去开真相机。
3. `AppHost.InitializeAsync` 在 `health.RunAll()` **之前**显式 `ConnectAll()`，
   成功记 `设备连接完成 {Online}/{Total}`，失败记 WRN + 失败清单。

S2.5（同一提交）：三个相机构造函数只赋值不连接；`SwappableCamera.SwapTo` 创建候选后
显式 `open()` 再判可用（工厂不再顺手连接，"连上"必须由切换逻辑负责）。见 W-02。

L1：`DeviceManager_ConnectAll_ReportsFailuresInsteadOfThrowing`（一抛异常 + 一"调了仍离线"，
断言两条都被报出且不抛）、`DeviceManager_ConnectAll_AllOnline_ReturnsNoFailure`、
`FileCamera_Construct_DoesNotScanDisk`（目录里有图也不许被扫进来，open 后才有）、
`SwapTo_OpensCandidate_BecauseFactoryOnlyCreates`、
`InitializeAsync_ConnectsDevicesBeforeHealthCheck`（自检发生时 `ConnectCount` 已是 1）。

L3 走查（临时副本 + File 相机，4 次运行）：

| 场景 | 结果 |
|---|---|
| 正常启动 | `[INF] 设备连接完成 2/2` → `[INF] 自检 设备连接: OK (2/2 在线)`；启动 7.1s |
| 二次启动 | 3.6s，`2/2 在线`，无「设备被占用」 |
| 连点 3 次关闭 | 收尾 1 份，304ms，退出码 0，残留进程 0 |
| 故障注入（相机目录不存在） | `[WRN] 设备连接完成 1/2，失败: 工业相机: 已调用 Connect 但仍未连接`；自检 FAIL 但不阻塞，窗口正常出现，退出码 0 |
| `FileCamera` 扫描次数 | 每次运行恰好 1 次（`刷新完成 … 找到 5 张图`），证明构造不再扫盘、只由 `open()` 触发 |

测试：UnitTests 199 + App.Tests 24 = **223**（新增 5 条）；告警 115 持平；`ci.cmd` 0。

### S3（2026-10-20）：Shell、导航、View/VM 边界（DONE，W-03/W-04/W-05/W-24 FIXED）

分两个提交，各自可编译、可启动：

**S3a 抽页面（行为不变）**

- 新增 `InspectionPageViewModel`（`ViewModels/InspectionPageViewModel.cs:19`）：持有五块面板子 VM + `IsRoiEditorOpen`，
  原图与覆盖层的互斥用派生属性 `IsImagePanelVisible`（不引入反向转换器）。
- 新增 `InspectionPageView`（`Views/InspectionPageView.xaml`）：把 `MainWindow.xaml` 里那段版面原样搬过来。
- F2 从 `MainWindow` 的 `KeyDown` + `ToggleEditMode()` 收进页面 VM 的命令；动态窗口标题取消。
- `MainWindow` 参数从 12 降到 6，`NavigateMessage` 处理器改为「切页 → `ExitEditMode()`」。
- 验收：`InspectionPageViewModelTests` 4 条（进入编辑交图、互斥、无图不抛、退出幂等）+ 真实启动/关窗。

**S3b 导航与 DI 归位**

- 新增 `INavigationService` / `NavigationService`：路由表写死两页（检测/相机），未知路由 false + WRN；
  页面实例构造注入，**没有 `IPageFactory`、没有 `IServiceProvider` 查询**。
- 新增 `ShellViewModel`（`ViewModels/ShellViewModel.cs:19`）：Sidebar / StatusBar / CurrentPage / F2 路由；
  离开检测页时调用 `ExitEditMode()`，覆盖层不会漂到相机页。
- `MainWindow` 只剩 1 个构造参数；XAML 三个 `ContentControl` 绑 VM（`MainWindow.xaml:20,29,31`）。
- `App.xaml:14-56` 九条 `DataTemplate` 完成 VM→View；`AddViews` 不再注册任何 View。
- 侧边栏导航从「`SelectionChanged` 事件」改成「`SelectedItem` 双向绑定」（`SidebarView.xaml:69-70`），
  `SidebarView.xaml.cs` 现在没有任何逻辑。

**过程中修掉的两个真问题**

1. `MainViewModel` 之前是被 `MainWindow` 注入才被创建的；窗口不再注入它之后，**没人解析它 = 没人订阅触发 = 不采图**。
   现在 `AppHost.InitializeAsync` 显式取一次（`AppHost.cs:162`），并写清理由。
2. 关注点：页面的子面板 VM 必须保持单例。`MainViewModel` 每帧直接写 `ImagePanel` / `StatusBar`，
   写成 Transient 会写到"界面上没显示的那一份"，表现为界面永远不动且无异常。

**验证**

L1：App.Tests 24 → **41**（新增 17：`InspectionPageViewModel` 4、`NavigationService` 4、`SidebarViewModel` 3、
`ShellViewModel` 4、`AppComposition` 2）；UnitTests 199 不变；合计 **240**；告警 115 持平；`ci.cmd` 0。

L3（临时副本 + File 相机，带一次性的可视化树导出，验完已删除、`git diff` 确认无残留）：

| 场景 | 结果 |
|---|---|
| 启动 | 7.7s，窗口出现，日志零 ERR/WRN，设备连接 2/2、自检 OK |
| 检测页可视化树 | `MainWindow dc=ShellViewModel` → `ContentControl` → `InspectionPageView dc=InspectionPageViewModel` → 五块面板视图齐全（`ImagePanelView` / `RoiEditorView` / `ResultPanelView` / `LogPanelView` / `ControlPanelView`）+ `SidebarView` + `StatusBarView` |
| 导航到相机页 | 树里只剩 `CameraPageView dc=CameraPageViewModel` + `SidebarView` + `StatusBarView`，检测页视图已卸载 |
| 导航回检测页 | 视图重新由模板创建，子 VM 仍是原实例（面板状态不丢） |
| 关窗 | 189-203ms，退出码 0，五行关闭日志齐全 |

**没验到的（记 NOT_RUN，不写 PASS）**

- **鼠标点击侧边栏、按 F2、连续切页 20 次**：本机 `Add-Type`（含 `UIAutomationClient`、`System.Drawing`、反射加载程序集）
  被安全策略禁用，无法注入输入、也无法截图。补偿：`SidebarViewModelTests` / `ShellViewModelTests` 覆盖同一批 VM 行为；
  视图层只验证到「模板能创建出正确的 View 且 DataContext 正确」。
- **设计器预览**：没有 Visual Studio 交互环境，只保证 `d:DataContext` 存在且 `ci.cmd` 不报 MC 告警。
- **切页后 `ImagePanelView` 的缩放倍数被重置**：视图由模板重建，属预期行为（VM 状态不丢），未做专门回归。

### S4（2026-10-20）：线程、工作流与日志（DONE，W-06~W-09/W-11 FIXED）

**结构**：`MainViewModel` 删除（-342 行），拆成三块

- `Services/Inspection/InspectionWorkflow`（211 行）：采图 → Pipeline → 标注 → 存图 → 存库，
  `RunOnceAsync` 整段 `Task.Run`；`finally` 释放 `frame.Image`；ROI 取快照（编辑器换引用不打断在途帧）。
- `InspectionPageViewModel`：触发入口（`SynchronizationContext.Post` 一次）→ 防重入（`Interlocked`）→
  在途租约 → 广播。**VM 内零 `Dispatcher` / 零 `Application.Current`**。
- 面板改为订阅 `InspectionResultMessage`（载荷换成 `InspectionRunResult`，带两张已冻结位图）。
  面板自己记账：`StatusBarViewModel` 维护 OK/NG 与 EMA 节拍，`ImagePanel` 自己切页签。

产品代码净 **-11 行**（新增 331，删 342），新增类型 3 个：`MatBitmap`（静态 helper）、
`InspectionRunResult`（返回值 record）、`IInspectionWorkflow`（1 方法 / 1 实现 + 1 测试替身）。

**过程中撞到的三个真问题**

1. **`WeakReferenceMessenger` 同步投递**：消息在哪个线程 `Send`，订阅者就在哪个线程收。
   检测挪后台后，`Send` 这一行会把 `ObservableCollection` 操作搬到后台线程；解法是让页面 VM 只在 UI 线程广播一次。
2. **`ICollectionView` 有 Dispatcher 亲和性**：日志面板用它在无 UI 线程的测试里抛
   `NotSupportedException`。改成自维护的 `VisibleEntries` 列表 —— 线程问题上少一个变量。
3. **`CameraFrame.Image` 从来没人释放**（`MainViewModel` 旧实现）：12MP 彩色帧一帧 36MB，
   靠 `Mat` 终结器兜底。现在由工作流 `finally` 释放，`InspectionWorkflowTests` 断言 `IsDisposed`。

**验证**

- L1：App.Tests 41 → **60**（新增 19：`MatBitmap` 5、`InspectionWorkflow` 4、编排 4、
  结果面板 4、状态栏 3；另删旧 `MainViewModelShutdownTests` 1 条，契约由编排测试承接）；
  UnitTests 199 不变；合计 **259**；告警 115 → 114（基线自动下调）；`ci.cmd` 0。
- App.Tests 增加 `[assembly: CollectionBehavior(DisableTestParallelization = true)]`：
  `WeakReferenceMessenger` 是进程级静态，并行跑时「我这个实例收到几条」不可判定。
- L3（临时副本 + File 相机 + 一次性 UI 线程探针，验完已删除、`App.xaml.cs` 无 diff）：

| 场景 | 结果 |
|---|---|
| 空闲 90s（对照） | 1440 样本，>100ms **0 次**，max **80ms** → 探针本身可信 |
| 自动模式 90s | 1350 样本，p99 **68ms**，>100ms **9 次**，max 3.2s；41 帧完成 |
| 单帧耗时 | 9~1470ms（配方含 Yolo ONNX，CPU 推理约 1s/帧） |
| 跨线程异常 | `InvalidOperationException` / 「另一个线程」 **0 命中** |
| 关窗 | 五步日志齐全，退出码 0 |

**如实记录的已知风险（不写成 PASS）**

- 那 9 次 >100ms（含 2 次 1.2~3.2s）与 ONNX 推理 / EF 首次建库的 CPU 饱和时段重合，**同一时段后台日志也整体停顿**，
  判定为整机调度饥饿而非 UI 线程执行检测；但「UI 在推理期间完全不受影响」这句话不成立，写在这里备查。
  真要压掉，得限制 ORT 线程数或降优先级 —— 属独立优化，不在本片。
- **鼠标/键盘/设计器仍不可验证**：本机 `Add-Type` 与 `Assembly.Load*` 被策略拦截，
  自动模式是临时用环境变量从内部触发的（代码已删）。真实点击「自动模式」按钮这一步记 `NOT_RUN`。
- **Hik 真机在后台线程释放**：无真机，只验 File 相机。

### S5（2026-10-20）：配置与路径（DONE，W-14/W-15 FIXED）

**三个决策（用户拍板）**

| 决策 | 结论 |
|---|---|
| 可写数据放哪 | **便携模式**：数据根 = exe 目录，`--data-root=路径` 显式改；不用 `%LocalAppData%` |
| 旧数据 | **全部删除，不迁移**（改造前数据都是走查产物）；迁移器写完又按此决定移除，未留下兼容分支 |
| `Trigger:Mode` | **删除**（死键）。澄清一处早期误判：`TimerIntervalMs` **有人读**（原 `ServiceCollectionExtensions.cs:72` 建 `TimerTrigger`），保留并纳入 `TriggerOptions` |

**结构**

- `Composition/AppOptions.cs`（新）：`CameraOptions`/`TriggerOptions`/`StorageOptions`/`DetectionOptions`/
  `RecipeOptions`/`LoggingOptions` + 复用 Service 层 `HousekeepingOptions`，聚合为 `AppOptions`；
  `AppOptionsBinder.Bind(config, dataRootOverride)` 是唯一绑定入口。校验失败抛
  `AppOptionsValidationException`（聚合全部错误，带键名与合法值），由 `App.StartAsync` 写 crash.log 并以 1 退出。
- `AppPaths`：构造收 `AppOptions`；可写路径（db/日志/图片）只基于 `DataRoot`；
  读资源（模板/图库/配方）exe 目录优先、cwd 兜底（只为 `dotnet run` 便利，资源只读）。
- `App.xaml.cs`：`ParseDataRoot(Environment.GetCommandLineArgs())` 支持 `--data-root=X` 与 `--data-root X`。
- `ServiceCollectionExtensions` / `CameraFactory`：签名由 `IConfiguration` 改为 `AppOptions`/`CameraOptions`；
  删除 `ReadHousekeepingOptions`（直读配置点归零）。

**过程中撞到的两个问题**

1. 框架自带的 `OptionsValidationException` 构造要求 `optionsType`，与"整份文件校验失败"的语义不合 ——
   改用自有异常，聚合错误列表（`.Errors`）。
2. 走查脚本里 `Start-Process -ArgumentList @()`（空数组）在 PS 5.1 下直接报参数校验错误，必须分支处理。

**验证**

- L1：App.Tests 60 → **77**（新增 17：`AppOptionsTests` 11、`AppPathsTests` 6）；UnitTests 199 不变；
  合计 **276**；告警 114 → **102**（Options 收口消掉重复读取，基线自动下调）；`ci.cmd` 0。
- L3（临时副本，cwd 换成无关目录）：

| 场景 | 结果 |
|---|---|
| 默认（cwd=无关目录） | 窗口出现、退出码 0；db/Logs/InspectedImages **全部落在 exe 目录**；cwd **0 个文件** |
| `--data-root=<自定义>` | 窗口出现、退出码 0；db/Logs 落在自定义目录 |
| 坏配置（`Camera:Mode=Basler`） | **退出码 1**、无窗口；`crash.log`：`Camera:Mode = "Basler" 不合法，可选值：Webcam, Hikvision, File` |

- 数据清理：旧 db/Logs/InspectedImages（项目根、`src/VisionStation.App`、`tests/VisionStation.Test`，合计约 3.4MB）
  与 TEMP 下 9 个走查副本已全部删除，复查为空。

**没验到的**

- 计划任务/服务方式启动（本机不便配置）：以"cwd 换任意目录仍落 exe 目录"为等价证据。
- 只读目录下启动（exe 目录不可写）失败信息是否可读：未构造，记 `NOT_RUN`。

### S6（2026-10-20）：资源、命名、注释（DONE，W-16/W-18/W-19 FIXED）

**资源拆分（W-16）**

- `Resources/Theme.xaml`：30 个命名画刷，分品牌色 / 判定色 / 文本 / 面板 / 深色侧栏 / 覆盖层 / 状态色七组。
- `Resources/Converters.xaml`：6 个转换器实例，全应用唯一注册点（原来视图里各有一份同名 key，会遮蔽应用级资源）。
- `Resources/Controls.xaml`：8 个样式，内部自带 `Theme.xaml` 合并（解析期就需要，不能依赖兄弟字典）。
- `App.xaml`：只剩三段合并 + 9 条 `DataTemplate`，150 行 → 60 行。
- 视图内十六进制色值 **52 → 0**（`StatusBarView.xaml:24` 剩一处是历史注释文本，非生效值）；
  `Background="White"` 一并改为 `CardBg`。删除无人使用的 `StringToColorConverter`。

**命名（W-18）**

`ICamera.open/close` → `Open/Close`，涉及 11 个文件：接口、四个实现（File/Webcam/Hik/Swappable）、
`CameraDevice`、`ControlPanelViewModel`、控制台程序、两个测试工程的替身与断言。残留小写调用 0。

**注释（W-19）**

新建 `docs/代码事故索引.md`，把 20 处 `F-xxx`（含 `R-xxx`）登记为「编号 → 现象 → 现在的做法」；
业务代码注释只留"为什么这样做"，不再出现编号。`R-003` 是 ADR 编号，保留并在索引里说明两套体系的区别。

**验证**

- `ci.cmd` 0；UnitTests 199 + App.Tests 77 = **276**（本片是 XAML/注释级改动，未新增用例）；
  告警 101 持平。
- L3 走查（临时副本 + File 相机）：启动 6.2s、窗口出现；`[ERR]/[WRN]` **0 条**；
  `XamlParseException` / `Cannot find resource` **0 命中**（资源字典拆分最容易在这里炸，必须真启动才证明）；
  关窗 191ms、五行关闭日志齐全、退出码 0。

**已知残留（如实记录，未粉饰）**

- 四个转换器内部仍用 `Color.FromRgb(...)` 硬编码色值（`LogLevelToBrushConverter.cs:16-19,42,57-59`）。
  它们不是 View，但"改主题要全仓搜"这条仍未 100% 消除。要彻底解决应把日志级别配色改成
  XAML `DataTrigger`（样式化），属独立小片，不在 S6 范围。
- 设计时预览未验证：本机无 VS 交互环境，只保证 9 个 View 都有 `d:DataContext`（S3b 已加）。

- 下一步：WPF 改造 S1~S6 全部完成。后续候选（未排期）：设置窗口（`usersettings.json`，只放改完立即生效的项）、
  转换器配色样式化、日志双轨合并（W-13）。

### S7（2026-10-22）：侧边栏导航（DONE）

**缺陷（实测，不是推断）**

两个 `ListBox`（上/下部导航）共享同一个 `SelectedItem` 双向绑定。用真 WPF 复现：把两块 ListBox
绑到同一属性，点下半部『相机』后，上半部『检测』的 `SelectedItem` 仍是『检测』——
两块列表各自记住自己的选中项、互不清除，于是**同时亮两个图标**，操作员判断不出当前页。
这不是把绑定写错，是"选中"这个状态本就不该由控件拥有。

配套的八个图标里只有两个有页面（`NavigationService` 只注册检测/相机），其余六个点了
只走 `_revertingSelection` 撤回分支 —— 摆出来就是六个假按钮。

**做法**

- 导航项从"列表选中"改成"按钮 + `NavItem.IsActive`"。视图不再持有"当前选中谁"，
  高亮单向来自 `INavigationService.CurrentRoute`（导航服务是唯一真相源，
  将来快捷键/外部触发换页，侧边栏也自动跟随）。
- `_revertingSelection` 防回环标志及其守护的整段退回逻辑删除，`SidebarViewModel.SelectedNavItem` 移除。
- 未实现的六个页签（参数/配方/历史/PLC/日志/系统）从侧边栏删除，等页面做出来再加。
- `NavItem.Route` 与 `NavItem.Label` 拆开：原先用中文 Label 当路由 key，改文案就会断导航。
- 导航按钮补 `AutomationProperties.Name`（模板里只有一个 `Path`，默认算不出可读名）。

**验证**

- `ci.cmd` 退出码 0；UnitTests 237 + App.Tests 84 = **321**，0 失败；告警 92 与基线持平。
- L3 走查（`dotnet` 直启 exe + UIA 真点击，非模拟）：启动时『检测』蓝、『相机』灰；
  点『相机』后『检测』转灰、『相机』转蓝、页面切走；点回『检测』后还原。三个状态各截图核对，
  **任一时刻只有一个图标高亮**。
- 关窗用不带 `/F` 的 `taskkill`，进程正常退出、无残留。

**测试改动（如实记录）**

`SidebarViewModelTests` 随契约重写：`SelectingUnimplementedPage_RevertsSelectionAndKeepsActivePage`
的前提（界面上有假页签）已被删除，改为 `EveryNavItem_NavigatesToAnImplementedRoute` ——
它会拦住"再往侧边栏塞一个点不动的图标"，比原用例更严。另加 `Single(IsActive)` 断言锁住"只亮一个"。

**已知残留**

- 走查时自检报「设备连接」异常（海康 `-2147483133`，强杀进程后的锁死态），与本次改动无关；
  相机页在设备打不开时是空白，没有"设备不可用"提示 —— 属独立问题，未处理。
- 侧边栏仍是 52px 纯图标 + ToolTip，没有文字标签。是否加宽到带文字属版面决策，待定。
- 自动运行时的整条侧边栏暗化（`design-mockup.html` 的 `.sidebar.dimmed`）仍只实现了中间那颗指示灯，
  背景/图标变色未做。


---

## S8 · 2026-10-23 检测引擎改造（架构层，含界面清理）

**改了什么**

自研算法工具链下线，判定交给引擎：`IInspectionEngine` 两个实现（VisionMaster 流程 / ONNX）。
新增两个工程 `VisionStation.Engines.Onnx`、`VisionStation.Engines.Vm`；删除 `Pipeline`/`IVisionTool`/
三个工具/`Calibration`/`CoordinateMath`/`RoiRegion`/`CalibrationConfig`/`ToolFactory`/ROI 编辑器与坐标封装，
共 11 个源文件；`tests/VisionStation.Test`（旧冒烟工程，测的是被删的 Pipeline）整体移除。

界面侧的连带改动（属本规范管辖范围）：
- 删除 ROI 编辑覆盖层（`InspectionPageView.xaml` 的双 ContentControl 互斥 + `IsImagePanelVisible`）。
- 删除窗口级 F2 快捷键与 `ShellViewModel.ToggleRoiEditor`、`InspectionPageViewModel.ExitEditMode`。
- 图像面板页签 4 → 2：`ROI` / `边缘` 两个页签从来没有独立图像源，点开显示的还是原图（与侧边栏那 6 个死图标同类问题）。
- 删除 `Messages.RoisUpdatedMessage`。

**验证**

- `ci.cmd` 退出码 0；UnitTests 143 + App.Tests 75 = **218**，0 失败；告警 92 → **81**，棘轮基线下调。
- 黄金样本库重跑：**判定完全一致，误报框数量无变化**（104/105 命中、错分 0、跨类误报 14），退出码 0。
- L3 走查（PrintWindow 抓窗口自身内容 + UIA 真点击）：
  1. 启动后自检五项全绿，其中 `检测引擎: Onnx · neu-det.onnx`、`配方文件: … · Onnx`；
  2. 点『开始检测』→ 图像区出现 YOLO 框与 `NG` 水印，结果面板 `缺陷 NG / 检出缺陷 2 处`，日志面板两条 NG 记录，
     状态栏 `FPS 0.4 / Cycle 662ms`（首帧含模型加载 2.9s）；
  3. 侧边栏仍只有『检测』『相机』两个入口，切到相机页再切回，页面与高亮正常；
  4. UIA 文本快照确认：`ROI` 页签不存在、`边缘` 页签不存在，`原图`/`检测` 存在。
- 关窗用不带 `/F` 的 `taskkill`，日志显示 5 段关闭流程全部完成（stop/cancel/在途收尾/释放容器/刷日志，共 844ms）。

**走查中临时改过的配置（已还原）**

虚拟相机处于被独占状态（`0x80000203`，上次强杀进程所致），为出帧把 `Camera:Mode` 临时改为 `File`，
并把激活配方 `default-2448.json` 的 `sensor` 临时改成 300×300（`TestImages` 是 300×300）。
两处均已还原为 `Hikvision` / `2448×2048`。

**已知残留（新）**

- ONNX 引擎对垫圈图会误报（NEU-DET 是钢材表面数据，样本 01_good.jpg 被判 NG，两处 scratch）。
  属模型与场景不匹配，链路本身正常；要上生产必须用本产品缺陷样本重训。
- VM 引擎只完成"编译通过 + 失败路径可解释"，**运行未验证**：缺一份可用的 `.sol` 与覆盖 SDK 二次开发的 License。

---

## S9 · 2026-10-23 调试期两块手工活自动化（调流程不再重启、映射表不再手抄）

**为什么要做**：调 `.sol` 是二次开发的日常 —— 改一个公差、存盘、回程序看效果。
没有重载时每改一次要关程序重开（要跑完自检与设备初始化）；`vm.outputs` 要手抄模块名，
抄错一个字符的后果是"找不到模块「圆查找1」"，而且要到第一帧才报。

**改了什么**

| 项 | 内容 |
|---|---|
| `IInspectionEngine.Reload()` | 新增契约。VM 实现 = 释放后立刻重载一次（不是"标记脏等下一帧"，调流程的人要当场知道能不能加载）；ONNX 实现 = 先建新推理器、成功才换旧 |
| `IOutputScanner` | **可选**接口，只有 VM 引擎实现。按配方里已声明的模块名核对"这个名字对不对、这个模块有哪些输出" |
| 控制面板按钮 | 底排 3 个 → 2×2 四个：重新连接 / **重载流程** / **扫描输出** / 清空 |
| 删除 `SaveImage` | 它只发一条日志，什么都不做 —— 与侧边栏那 6 个死图标同一类问题 |

**为什么扫描不做"列出全部模块"**：SDK 的 `GetAllModuleList` 返回 `ModuleInfoList`
（`nNum` + `ModuInfo[]`），而 `ModuInfo` 只有 `nModuleID` / `nProcessID`，**没有模块名**。
拿不到名字就没法写进配方。改为用配方里已有的模块名去核对，反而正是要解决的问题。
可用的入口是 `VmModule.Outputs`（`VmIOCollection`，实现 `IList<VmIO>`，可直接枚举）。

**验证**

- `ci.cmd` 退出码 0；UnitTests 143 + App.Tests 80 = **223**，0 失败；告警 81 → **78**。
- 新增 6 项用例：重载停触发并退出自动模式、重载失败不抛、不支持扫描的引擎不崩、
  扫描把配方模块名传给引擎、扫描 json 的 ioKind 推断、ioKind 圆的输出不能被当成 Float。
- L3 走查两个分支（截图：`rl-2-scan.png` / `rl-3-vm-reload-fail.png`）：
  1. **ONNX 引擎**点“扫描输出” → 日志 Warning：`当前引擎（OnnxYolo）没有可扫描的模块输出，此功能只对 VM 流程有效`；
  2. **VM 引擎 + .sol 缺失** → 启动自检即报 `检测引擎: VM 流程文件不存在`（状态栏“自检 1 项异常”），
     点重载 → Error：`引擎重载失败：VM 流程文件不存在：…\Config\solutions\washer-2448.sol（在 VisionMaster 里搭好流程并另存为 .sol）`。
     原因、绝对路径、下一步动作三样都在，不需要查代码。
- 走查期间临时把激活配方 `default-2448.json` 的 `engine` 改成 `Vm`，**已还原为 `Onnx`**。
- 关窗用不带 `/F` 的 `taskkill`；顺带确认虚拟相机锁已释放（`OpenDevice=0`），可直接启动。

**已知残留**

- `Scan` 的“模块存在且能枚举输出”这一条**仍未在真实 `.sol` 上验证**（当前没有 .sol）。
  已验证的是它的失败路径（文件不存在、引擎不支持）与 6 项纯逻辑用例。
