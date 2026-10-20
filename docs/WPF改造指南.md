# WPF 改造指南（施工指令）

> **用途**：交给 AI 或人直接执行。每一片独立可编译、可回滚、有验收标准。
> 已完成的切片不重做，只说明"现状是什么样"，供后续接线时对齐。
>
> 规则全文：`docs/WPF开发规范.md`。偏差状态与证据：`docs/WPF规范落地记录.md`（W-01~W-24）。

---

## 0. 执行原则

1. **一次只做一个切片**。一个切片 = 可编译 + 可回滚 + 有验证证据，不搞大爆炸重构。
2. **先护栏，后结构；先所有权，后线程；先行为，后外观**。
   没有 App 层测试，不改生命周期；对象由谁创建/释放没定，不往后台搬。
3. **不跨层顺手改**。App 切片不碰 Core/Devices 的判定逻辑和设备构造；设备层的构造副作用单独开切片。
4. **测试只加，不改断言**。需要改断言才能过，说明代码错了或切片切错了，停下来。
5. **业务参数问人**。阈值、保留天数、容量上限、急停确认策略，不由开发/AI 拍板。
6. **每片结束必须**：`ci.cmd` 退出码 0 + 实际启动走查 + 更新落地记录。只贴 build 绿不算完成。
7. **切片膨胀就拆**。超过约 10 个文件或 400 行 diff，先拆成两个能独立编译的提交。

### 0.1 三条铁律（违反即重做）

1. **不为"让某一步可被断言"造类型。** 此前用 Generic Host 的思路把关闭流程拆成 12 个类型、
   7 个文件，包住 40 行逻辑，全部删除、净减约 600 行。
   判据：**有第二个实现吗？没有、也不跨进程 → 不要接口。**
2. **每个新增类型必须回答"谁 new、在哪条线程改、活到什么时候"。** 答不上来就别加。
3. **不用 Web 的套路解决桌面问题。** 具体：不引入 Scoped、不把启停交给 `IHostedService`、
   View 不进 DI、导航不用 Messenger + `Visibility`。

---

## 1. 现状总图

```text
S0 基线冻结                       DONE
 └─ S1 测试护栏 + 最小抽象        DONE（W-10/12/17/22/23）
     └─ S2 组合根/启动/关闭       DONE（W-01/02/20/21 全部 FIXED）
         └─ S3 Shell/导航边界      OPEN（diff 最大）
             └─ S4 线程/工作流     OPEN（技术上最难）
                 └─ S5 配置/路径    OPEN（含需拍板决策）
                     └─ S6 资源/命名  OPEN
```

| 切片 | 内容 | 状态 | 对应偏差 |
|---|---|---|---|
| S0 | 记录改造前基线 | DONE | — |
| S1 | App 测试工程、`IDialogService`、`IRecipeStore`、去 VM 弹窗与文件 IO、删图像双写 | DONE | W-10/W-12/W-17/W-22/W-23 |
| S2 | 组合根三段、异步建库、有序关闭、应用级取消、设备连接时机 | **DONE** | W-01（设备部分）/W-02/W-20/W-21 |
| S3 | View 退出 DI、`DataTemplate` 导航、页面 VM Transient | OPEN | W-01（App 部分）/W-03/W-04/W-05/W-24 |
| S4 | 检测/存图移出 UI 线程、位图 `Freeze`、日志 sink、结果面板数据驱动 | OPEN | W-06/W-07/W-08/W-09/W-11/W-13 |
| S5 | 强类型 Options、可写路径分离、热重载决策 | OPEN | W-14/W-15 |
| S6 | 资源字典、去硬编码色、命名、注释 | OPEN | W-16/W-18/W-19 |

### 1.1 已完成的现状基线（后续接线必须对齐，不要重造）

**组合根**：`Composition/AppHost.cs` 四段——`Configure()`（配置/路径/Serilog/配方/DI，不连设备不建库）
→ `InitializeAsync(ct)`（建库/自检/启动管家，不碰 UI，失败抛给 `App` 以非 0 退出）
→ `Show()`（创建 `MainWindow`、挂 `Closing`、Show）→ `StopAsync(timeout)`（幂等）。
`IAppWindow` 只为让 `AppHost` 在测试里不依赖 WPF。

**关闭顺序**（`AppHost.StopAsync`，直线代码，每步一行带编号日志）：

1. 停触发 `ITriggerSource.Stop()`
2. `ShutdownState.BeginStop()`（bool + CTS，幂等）
3. `InFlightWorkMonitor.WhenIdleAsync(剩余预算)` —— 必须真异步。关窗处理器在 UI 线程，
   在途帧 `await` 的续体需要 UI 线程才能继续；`Thread.Sleep` / `.Wait()` 会占死 UI，永远等不完。
4. `Task.Run(() => 释放容器)` —— 必须在后台：`HousekeepingService.Dispose` 内部同步等 5 秒。
5. 写摘要 → `Log.CloseAndFlushAsync()` —— **摘要必须在 flush 之前**。
6. 通知看门狗 clean exit（不写日志，flush 之后 sink 已关）。

每步用 `Guarded` 包住：抛了就记一笔、继续下一步，不阻断。预算 8s（实测 279~375ms）。

**设备连接只有一个入口**：`AppHost.InitializeAsync` → `DeviceManager.ConnectAll()`（返回失败清单，
连接后还要查 `IsConnected`，因为各实现的 `Connect` 自己 catch 掉了"打不开"）。不在 DI 工厂、不在相机构造函数。
三个相机构造函数只赋值，连接一律走 `open()`；`SwappableCamera.SwapTo` 对候选显式调 `open()` 再判可用。

**生命周期只有两个类，都无接口**：`ShutdownState`（bool + CTS）、`InFlightWorkMonitor`（租约/归零唤醒/带超时）。
**不要再引入 `IAppLifetime` / `IShutdownStages` / `IWatchdogSignal`。**

**测试**：`tests/VisionStation.App.Tests`（`net8.0-windows` + `UseWPF=true`，不建 `Application`、不建窗口，
VM 在普通线程构造）；`tests/VisionStation.UnitTests` 保持 `net8.0`。当前 199 + 24 = **223** 项，告警基线 115。

**S1 遗留的三个坑**：

1. App.Tests 构建要加 `--no-dependencies`：App 已在 ci 第 1/4 步构建过，重复构建会经 WPF `_wpftmp`
   再产一份告警，把基线从 129 顶到 161。
2. 只在 View 里出现的东西不许为了测试抽接口。`IDialogService` / `IRecipeStore` 抽出来是因为它们
   有第二个实现或跨进程边界；只有一件事的转换不抽（见 4.1 的 `MatBitmap`）。
3. S1-5 缩放/平移仍 `NOT_RUN`（鼠标输入自动化被本机策略禁用），不要写成 PASS。

---

## 2. S2（已完成，勿重做）

S2.1~S2.5 全部 DONE，W-01（设备部分）/W-02/W-20/W-21 已 FIXED。现在的形态见 §1.1，**不要重新设计**：

- 组合根四段、关闭六步直线代码、生命周期两个类 —— 这三条已经定下来。
- 设备连接只有 `AppHost.InitializeAsync` → `ConnectAll()` 一个入口，且必须早于自检
  （`HealthChecker` 读的是 `DeviceManager.AllConnected`）。
- 相机构造函数不连接；`open()` 才是连接；`SwapTo` 负责给候选调 `open()`。

证据：落地记录「S2.4（2026-10-20）」段。

---

## 3. S3：Shell、导航、View/VM 边界（diff 最大的一片）

拆成两个可独立编译的提交：S3a 导航骨架，S3b 去 View DI。

### 3.1 目标形态

```text
MainWindow (View，无业务依赖)
 └─ DataContext = ShellViewModel
     ├─ Sidebar
     ├─ StatusBar
     └─ CurrentPage  ← INavigationService
          ├─ InspectionPageViewModel → InspectionPageView
          │    ├─ ImagePanelViewModel / ResultPanelViewModel / LogPanelViewModel
          │    ├─ ControlPanelViewModel
          │    └─ RoiEditorViewModel（覆盖层）
          └─ CameraPageViewModel → CameraPageView
```

- `MainWindow` 不再注入 8 个 View（`MainWindow.xaml.cs:15-44`）。
- 页面切换走 `INavigationService`，不再用 `NavigateMessage` + `Visibility`
  （`SidebarViewModel.cs:52`、`MainWindow.xaml.cs:56-62`）。
- View 不进 DI；页面 VM 默认 Transient；Shell/StatusBar 等跨页状态可以是 Singleton。
- `IServiceProvider` 只允许出现在组合根；VM 里不允许 `GetRequiredService`。

### 3.2 S3a 导航骨架

1. 新增 `INavigationService` / `NavigationService`：`Navigate(string route)`、`object? CurrentPage`、
   `string CurrentRoute`；**未知路由明确失败**（抛异常或返回 false），不要静默什么都不做。
2. 页面 VM 由 `NavigationService` 内部 `new`（路由 → VM 的 switch）。
   **不建 `IPageFactory`**：页面 VM 目前没有构造参数，多一层工厂等于绕开 `DataTemplate`；
   等真出现"打开某条记录详情"这类带参数的页面再加。
   - `"检测"` → `InspectionPageViewModel`；`"相机"` → `CameraPageViewModel`；
   - 其余路由：**先禁用**或显示占位页，行为要由人确认，不能假装能导航。
3. `SidebarViewModel` 改成调用 `INavigationService.Navigate(route)`，`ActivePage` 从导航状态推导；
   删除 `NavigateMessage`（`Messages.cs:68-75`）。
4. `MainWindow.xaml` 改成三个 `ContentControl`：

```xml
<ContentControl Grid.Column="0" Content="{Binding Sidebar}" />
<ContentControl Grid.Row="0" Content="{Binding CurrentPage}" />
<ContentControl Grid.Row="1" Content="{Binding StatusBar}" />
```

5. `MainWindow.xaml.cs` 只保留 `InitializeComponent()` 和 `DataContext`；删除 `Visibility` 切换、
   `NavigateMessage` 订阅、标题改写。
6. F2 改成命令：`InspectionPageViewModel.ToggleRoiEditorCommand`，XAML 用 `KeyBinding`；
   `IsRoiEditorOpen` 通过 `BooleanToVisibilityConverter` 控制覆盖层。

### 3.3 S3b 页面组合与去 View DI

1. 新增 `InspectionPageViewModel`，持有 `ImagePanel` / `ResultPanel` / `LogPanel` / `ControlPanel` /
   `RoiEditor` 子 VM 和 `IsRoiEditorOpen`。子 VM 由页面内创建；**子 VM 之间不互相引用**，
   跨页 VM 更不允许。父页面 VM 持有子 VM 是组合关系，不算跨页通信。
2. 新增 `InspectionPageView.xaml`，用 `ContentControl Content="{Binding ImagePanel}"` 等把版面搬过去；
   `MainWindow` 不再知道这些面板。
3. 所有 View 改无参构造：删除 `DataContext = vm`；需要 VM 时从 `DataContext` 取；
   加 `d:DataContext="{d:DesignInstance ...}"` 保证设计器可预览（W-24）。
4. `App.xaml` 加 `DataTemplate`（VM 类型 → View），这是 WPF 的现成机制，不要用工厂绕：

```xml
<DataTemplate DataType="{x:Type vm:InspectionPageViewModel}">
    <views:InspectionPageView />
</DataTemplate>
<DataTemplate DataType="{x:Type vm:CameraPageViewModel}">
    <views:CameraPageView />
</DataTemplate>
```

5. DI：删除 `AddViews` 里所有 View / `MainWindow` 注册；页面 VM 用 `AddTransient`；
   Shell / StatusBar / 服务保持 `AddSingleton`；`MainWindow` 由 `AppHost.Show()` 直接 `new`。
6. 每个页面至少一个设计时 VM 或 `d:DataContext`，设计器不能空白。

### 3.4 测试与验收

测试：`NavigationServiceTests`（路由 → 预期 VM 类型、未知路由行为、重复导航是否复用/新建符合声明）、
`SidebarViewModelTests`（点击 → `Navigate` 被调用、`ActivePage` 更新）、
`AppCompositionTests`（容器里没有 `Window` / `UserControl` / View 类型；页面 VM 是 Transient）。
**不写 `MainWindow` 单元测试**，窗口行为靠走查。

验收：连续切页 20 次内存/句柄不持续增长；离开页面后后台刷新停止（当前只有相机页，先确认无常驻定时器）；
View 不在 DI；代码里搜不到 `NavigateMessage` 和用 `Visibility` 切页面；F2 编辑 ROI 正常、切页自动退出编辑；
设计器能预览"检测""相机"两页；`ci.cmd` 0 + 启动走查。

---

## 4. S4：线程、工作流与日志（技术上最难）

> 工业软件最硬的一条：UI 线程只做 UI。当前整条检测链路在 UI 线程（`MainViewModel.cs:141-235`）。

### 4.1 抽出 `IInspectionWorkflow`

目标：把"采图 → Pipeline → 标注 → 存图 → 存库"从 VM 移走。

```csharp
public interface IInspectionWorkflow
{
    Task<InspectionRunResult> RunOnceAsync(CancellationToken ct);
}

public sealed record InspectionRunResult(
    InspectionReport Report,
    BitmapSource? OriginalImage,
    BitmapSource? AnnotatedImage,
    long CycleMs,
    bool ImageSaved,
    string? ImageError);
```

- 内部用 `Task.Run` 或全异步 API，**不访问 `Dispatcher`**；
- `BitmapSource.Create` 后立即 `Freeze()`，再返回；
- `Mat` 的所有权在方法内结束，不跨线程共享；
- 工作流不更新 UI 状态，只返回结果。

`IInspectionWorkflow` 放 App 层（`App/Services`），它是呈现编排；真正的采图/判定/持久化若下沉到
`VisionStation.Service`，Service 仍不得引用 WPF。
位图转换用 App 里一个**静态 helper**（`MatBitmap.ToBitmapSource`，两三个方法），后台线程创建并 `Freeze()`。
**不抽 `IBitmapAdapter` 接口**：只有一件事、没有第二个实现、不跨进程，验证 Freeze 直接断言
`result.OriginalImage!.IsFrozen` 即可。

### 4.2 `InspectionPageViewModel` 只做编排

- 订阅 `ITriggerSource.Triggered`，`async void` 事件处理器包住全部异常；
- 防重入用 `Interlocked.CompareExchange` 或 `SemaphoreSlim(1,1)`，复位放 `finally`；
- `await _workflow.RunOnceAsync(_cts.Token)`；
- 结果通过 `InspectionResultMessage` 广播（**通知，不是控制流**），不直接写子 VM 属性；
- 删除 `Application.Current.Dispatcher.CheckAccess()` / `InvokeAsync`；
- 删除 `_imagePanel` / `_statusBar` 字段，改由各面板订阅消息。

### 4.3 面板改为订阅者

- `ImagePanelViewModel` 订阅 `InspectionResultMessage`，更新 `OriginalImage` / `AnnotatedImage`；消息里带已冻结的位图。
- `StatusBarViewModel` 订阅同一消息，更新 OK/NG/Cycle/FPS；不再由 `MainViewModel` 直接赋值。
- `ResultPanelViewModel` 改成数据驱动：
  - 遍历 `report.Results`，不要写死"特征有无/圆孔直径/划痕/缺口"四行（`ResultPanelViewModel.cs:50-79`）；
  - VM 只暴露 `IsOk` / `StatusText` / `Value` 等语义，颜色由 XAML `DataTrigger` 决定；
  - 删除 VM 里的十六进制颜色（`ResultPanelViewModel.cs:36,83,103`）。
- **不引入 `IUiDispatcher`**：唯一消费者是日志面板，而本片要做的 `UiLogSink` 内部自己
  `Dispatcher.BeginInvoke`，VM 根本不碰 Dispatcher。sink 做完如果出现第二个消费者，再谈接口。
- `LogPanelViewModel`：`Entries.Refresh()` 节流/合批（100~250ms 一次），不要每条日志刷新；
  后续改成 Serilog `UiLogSink`（有界队列），消除 `LogMessage` 与 Serilog 双轨（W-13）。

### 4.4 测试与验收

测试：`InspectionWorkflowTests`（正常帧 → 返回报告/图片/耗时；采图为 null → 返回失败/跳过，不抛；
Pipeline 抛异常 → 被捕获并上报；取消令牌 → `OperationCanceledException` 可预期，不吞掉）、
`InspectionPageViewModelTests`（两个并发触发只跑一次；处理中关闭 → 取消并等待，不更新已释放对象；
异常后防重入标志复位）、位图 `Assert.True(result.OriginalImage!.IsFrozen)`、
`ResultPanelViewModelTests`（2 个工具、5 个工具的配方都能按报告生成行，不依赖固定工具名）。

验收：自动运行 5 分钟界面可拖动/切页，无连续 100ms 以上卡顿；检测/存库期间 UI 线程没有
`Pipeline.Run` / 文件 IO / DB；运行中关窗不抛"跨线程访问已释放对象"；
`ViewModels` 下搜不到 `Application.Current` / `Dispatcher`；`ci.cmd` 0 + 启动走查。

---

## 5. S5：配置与路径（含需拍板的决策）

> 这一片有现场部署决策，先问人，不要按"常见值"改。

### 5.1 强类型 Options

在 `AppHost.Configure` 里绑定并校验：`CameraOptions`（Mode、ImageFolder、FileFilter、Loop）、
`TriggerOptions`（Mode、TimerIntervalMs）、`StorageOptions`（Mode、ImageFolder、DbPath）、
`HousekeepingOptions`、`LoggingOptions`（LogFolder、保留数量）、`RecipeOptions`（Path）。

校验失败在启动期写明确日志并非 0 退出，**不静默回落默认值**。业务参数（保留天数、容量上限）仍按
`AGENTS.md` 第 4 节问人。

### 5.2 去掉散落的 `IConfiguration`

- `CameraFactory.cs:29,43,44` 改为接收 `CameraOptions`；
- `AppPaths.cs:41-46` 改为接收 `PathOptions` / 明确路径；
- `ServiceCollectionExtensions.cs:62,67,93,132-137` 改为从 Options 取值；
- `reloadOnChange`（`App.xaml.cs:67`）要么接 `IOptionsMonitor` 并让消费方响应，要么关掉；
  没有消费者就关掉（W-14）。
- `Trigger:Mode`（`appsettings.json:9`）当前无人读取：删除或接通，**先问人**，不能顺手改行为。

### 5.3 可写路径（W-15，需决策）

`AppPaths` 基于 `Environment.CurrentDirectory`（`AppPaths.cs:63-80`），快捷方式/计划任务启动时路径不可控。
需人拍板：

- 方案 A（推荐）：可写数据放 `%LocalAppData%\VisionStation`，安装资源基于 `AppContext.BaseDirectory`；
- 方案 B：保留便携模式，但必须显式声明（启动参数/配置项），不能靠 cwd 猜；
- 现有 `visionstation.db`、`Logs/`、`InspectedImages/` 的迁移/兼容策略。

**未拍板前只做 Options 重构，不改默认落盘位置。**

### 5.4 验收

从不同 cwd 启动（快捷方式/命令行）写入目录符合决策；配置缺项/非法 → 启动失败信息可读；
没有 VM/Service 直接依赖 `IConfiguration`；`ci.cmd` 0 + 启动走查。

---

## 6. S6：资源、命名、注释

可以穿插，但**不要在 S3/S4 同时改命名和结构**，否则 diff 无法审查。

- `App.xaml:6-114` 拆成 `Resources/Theme.xaml`、`Controls.xaml`、`Converters.xaml`；
- 去掉 View 里的硬编码颜色（如 `SidebarView.xaml:4`、`CameraPageView.xaml:4-5`），改用命名资源；
- Converter 只注册一次：`App.xaml:20` 与 `StatusBarView.xaml:7` 的重复注册合并（W-16）；
- `ICamera.open/close` → `Open/Close`（`ICamera.cs:32,35`）单独切片，涉及 Devices 与测试，不和 S3 混；
- 业务代码里的 `修复（F-...）`/事故编号移到 `docs/` 或 `audit/`，代码只留"为什么"（W-19）；
- 每个页面补设计时数据，保证 XAML 设计器不是空白。

验收：View 中无新增硬编码色值；资源键无重复；`ci.cmd` 0；实际启动走查主题/日志面板。

---

## 7. 停止线（遇到就停，不要自行决定）

1. 需要改现有测试断言才能过；
2. 需要改判定阈值、保留天数、容量上限、急停确认策略；
3. 需要决定导航选型、未实现页面行为、页面缓存策略；
4. 需要决定可写数据路径或旧数据迁移；
5. 需要改 `HikCamera` / `FileCamera` 构造函数契约（会动设备测试前提）；
6. UI 无法实际启动走查，却想判"完成"；
7. 一个切片超过约 10 个文件或 400 行 diff，却不想拆；
8. **一个切片需要新增超过 2 个类型来"让某一步可被断言"** —— 这是第 0.1 节第一条铁律的起点。

---

## 8. 每片交付格式

```text
切片：Sx
改动：
未改：
验证：
  ci.cmd：退出码 / 告警数 / 测试数
  App.Tests：新增用例 / 全绿
  实际启动走查：启动 / 切页 / 相机重连 / 自动运行 / 关窗 / 重启
  git diff --stat：文件数 / 行数
风险与回滚：
落地记录：W-xx 状态 / 证据路径
```

没有"基本通过""应该没问题"。做不到的写 `NOT_RUN` 并说明缺什么。

---

## 9. 当前执行点

**S1 DONE → S2 DONE → 下一步 S3（View 退出 DI + `DataTemplate` 导航）。**

当前基线：UnitTests 199 + App.Tests 24 = **223**，告警 115，`ci.cmd` 0。

做 S3 之前不要碰线程（S4）、设备构造函数（已定案，见 §2）和配置路径（S5）。
S3 会同时改 `MainWindow` / `Views` / `ViewModels` / DI 注册，diff 大，先按 §3.2 / §3.3 拆成两个提交。
