# WPF 改造指南（执行顺序与切片）

> **目标状态**：`docs/WPF开发规范.md`（规则，MUST/SHOULD）。
> **当前偏差与证据**：`docs/WPF规范落地记录.md`（W-01 ~ W-24，只写现状）。
> **本文件只回答**：先改什么、后改什么、每片改哪里、怎么验收、什么时候必须停。
>
> 三份文件分工不要混：规范写“应该怎样”，落地记录写“现在怎样”，本指南写“按什么顺序改”。
> 偏差编号、证据、状态仍以落地记录为准，本指南不重复维护状态。

---

## 0. 执行原则

1. **一次只做一个切片**。一个切片 = 可编译 + 可回滚 + 有验证证据，不搞“大爆炸重构”。
2. **先护栏，后结构；先所有权，后线程；先行为，后外观**。
   没有 App 层测试，不改生命周期；对象由谁创建/释放没定，不往后台搬。
3. **不跨层顺手改**。App 切片不碰 Core/Devices 的判定逻辑和设备构造；设备层的构造副作用单独开切片。
4. **测试只加，不改断言**。需要改断言才能过，说明代码错了或切片切错了，停下来。
5. **业务参数问人**。阈值、保留天数、容量上限、急停确认策略，不由开发/AI 拍板。
6. **每片结束必须**：`ci.cmd` 退出码 0 + 实际启动走查 + 更新落地记录。只贴 build 绿不算完成。
7. **切片膨胀就拆**。超过约 10 个文件或 400 行 diff，先拆成两个能独立编译的提交。

---

## 1. 改造总图

```text
S0 基线冻结
 └─ S1 测试护栏 + 最小抽象 + 具体缺陷（P1）
     └─ S2 组合根 / 启动 / 关闭（P2a）
         └─ S3 Shell / 导航 / View-VM 边界（P2b）
             └─ S4 线程 / 工作流 / 日志（P3）
                 └─ S5 配置 / 路径（P4，含待决策）
                     └─ S6 资源 / 命名 / 注释（P5）
```

| 切片 | 目标 | 关键文件 | 依赖 | 对应偏差 |
|---|---|---|---|---|
| S0 | 记录改造前基线 | 无代码 | — | — |
| S1 | App 测试工程、`IDialogService`、`IRecipeStore`（Service 层）、去 VM 弹窗、修具体缺陷（S1-1~S1-6 DONE；S1-5 缩放/平移 NOT_RUN） | `tests/VisionStation.App.Tests`、`App/Abstractions`、`App/Infrastructure`、`Service/Recipes`、部分 VM/View | S0 | W-10/W-12/W-17/W-22/W-23 |
| S2 | 组合根拆三段、异步建库、有序关闭、应用级取消（S2.1 DONE；S2.2~S2.5 OPEN） | `App.xaml.cs`、`App/Composition`、`ServiceCollectionExtensions.cs` | S1 | W-20/W-21（W-01/W-02 的设备部分另开切片） |
| S3 | View 退出 DI、DataTemplate 导航、页面 VM Transient、去 code-behind 导航 | `MainWindow.*`、`App.xaml`、`App/Views/*`、`Navigation`、页面 VM | S2 | W-01（App 部分）/W-03/W-04/W-05/W-24 |
| S4 | 检测/存图移出 UI 线程、位图 `Freeze`、日志 sink、结果面板数据驱动 | `MainViewModel`/`InspectionPageViewModel`、`ImagePanel*`、`ResultPanel*`、`LogPanel*`、`StatusBar*` | S3 | W-06/W-07/W-08/W-09/W-11/W-13 |
| S5 | 强类型 Options、可写路径分离、热重载与 `Trigger:Mode` 决策 | `AppPaths`、`CameraFactory`、`ServiceCollectionExtensions`、`appsettings.json` | S2（可与 S3/S4 并行，但路径决策先问人） | W-14/W-15 |
| S6 | 资源字典、去硬编码颜色、命名、事故编号移出业务代码 | `App.xaml`、`Views/*.xaml`、`Docs`、`HealthChecker`/`HousekeepingService` 注释 | S3/S4 | W-16/W-18/W-19 |

---

## 2. S0：基线冻结（不改代码）

目的：后面每一片都要能回答“比改造前好了什么”，没有基线就只能凭感觉。

执行：

```bat
git rev-parse HEAD
git status --short
cmd /c ci.cmd
dotnet run --project src\VisionStation.App
```

记录到 `docs/WPF规范落地记录.md` 第 7 节，至少包含：

- 当前提交号、工作区是否有未提交改动；
- `ci.cmd` 输出：构建结果、告警基线、测试数量（当前 194）；
- 实际启动：启动耗时、默认相机模式、能否切到“相机”页、F2 是否进出 ROI 编辑；
- 自动运行 30 秒是否卡顿；
- 运行中关窗：进程是否在 5 秒内退出，再次启动是否报设备占用；
- 已知失败项直接写 `FAIL` / `NOT_RUN`，不写“基本正常”。

**验收**：`ci.cmd` 退出码 0；基线记录已追加；`git diff` 里没有业务代码改动。

---

## 3. S1：测试护栏 + 最小抽象 + 具体缺陷

> **当前进度**：S1 收口完成（S1-1~S1-6）；S1-5 缩放/平移仍 `NOT_RUN`；S2.1 DONE，下一步 S2.2。
> 这是第一刀，不要先动 `MainWindow` 和导航。没有 App 层测试时改生命周期，只能靠肉眼回归。

### S1-1 测试工程 + CI 接线（DONE）

- 新增 `tests/VisionStation.App.Tests`：
  - `net8.0-windows`、`UseWPF=true`、`PlatformTarget=x64`、`Nullable/ImplicitUsings=enable`；
  - 引用 App/Core/Devices/Service；xUnit；
  - 不创建 `Application`、不创建窗口；VM 在普通线程构造。
- **不要改** `tests/VisionStation.UnitTests` 的 `net8.0`：它继续覆盖 Core/Storage/Service，避免把非 UI 测试拖进 WPF。
- `Directory.Build.props`：`VisionStation.App.Tests` 不进 Sonar。
- `VisionStation.slnx`：加入新测试工程。
- `ci.cmd`：构建并运行 App.Tests。
  - App.Tests 构建用 `--no-dependencies`：App 已在 1/4 构建，重复构建会经 WPF `_wpftmp`
    再产出一份告警，把 129 基线顶到 161。
- 已加 2 个 `ImagePanelViewModelTests`，只用于验证护栏和锁 `CurrentImage` 契约；
  业务相关的 VM 测试在 S1-3/S1-4。
- 验收：`ci.cmd` 0；告警 129 持平；UnitTests 192 + App.Tests 2。

### S1-2 `IRecipeStore` + `JsonRecipeStore`（W-23 前半）DONE

新增：

- `VisionStation.Service/Recipes/IRecipeStore.cs`
- `VisionStation.Service/Recipes/JsonRecipeStore.cs`

接口：

```csharp
public interface IRecipeStore
{
    Task<string> SaveAsync(RecipeConfig recipe, CancellationToken ct = default);
}
```

`JsonRecipeStore`：

- 静态 `JsonSerializerOptions`：`WriteIndented = true`、`PropertyNamingPolicy = CamelCase`；
- 写唯一临时文件，再 `File.Move(..., overwrite: true)`；
- 成功后返回最终路径；失败清理临时文件，异常继续抛给 VM；
- 可选 `ILogger<JsonRecipeStore>`，只用于临时文件清理失败，避免空 `catch` 触发 S108/S2486。

测试放 `VisionStation.UnitTests`（不依赖 WPF）：

- 保存后 JSON 是 camelCase，且能读回；
- 目录不存在时自动创建；
- 目标路径非法时临时文件被清理。

DI：在 `AddAppServices` 注册 `IRecipeStore`，路径用 `appPaths.RecipePath`。

验收：UnitTests 全绿；`ci.cmd` 0。
实测：UnitTests 195（新增 3），App.Tests 2，告警 129 持平。

### S1-3 `IDialogService` + 控制面板（W-10 / W-22）DONE（代码/测试/UI PASS）

新增：

- `App/Abstractions/IDialogService.cs`
- `App/Infrastructure/WpfDialogService.cs`

接口只保留 S1 需要的三个方法：

```csharp
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message);
    Task ShowInfoAsync(string title, string message);
    Task ShowErrorAsync(string title, string message);
}
```

`WpfDialogService`：

- 内部才允许 `MessageBox`；
- 调用方不在 UI 线程时，用 `Dispatcher.InvokeAsync` 切回去；
- 不放进 VM。

`ControlPanelViewModel`：

- 构造函数加 `IDialogService`；
- `EmergencyStopAsync`：取消确认 → 直接返回，不 `Stop()`；确认 → `Stop()`、`IsAutoMode = false`、发状态消息；
- `Reconnect`：删除 `_camera is FileCamera`，改为 `_camera.open()`，再更新 `CameraOnline`。

App.Tests：

- 急停取消 → `Stop` 未调用；
- 急停确认 → `Stop` 调用一次；
- 重连 → `open()` 调用一次、`CameraOnline` 跟随 `IsAvailable`。

验收（目标）：`ControlPanelViewModel` 下搜不到 `MessageBox`；急停取消/确认与重连实际走查通过。
本轮实际走查：
- 启动：临时 File 模式副本自检 5 项 OK；
- 重连：ControlPanel 重连后日志出现 `[FileCamera] 刷新完成`；
- 急停取消：对话框返回 `No`，未停止；
- 急停确认：对话框返回 `Yes`，`AutoButtonText` 从“自动运行中”回到“自动模式”；
- 相机页重连：日志同样出现 FileCamera 刷新。
W-10 整体在 S1-4 关闭。

边界：本轮只走 `ICamera.open()` 的既有语义（File 重扫、Hik 重开）；
Webcam 在 `_opened` 且连续读帧失败后的重开仍是已知缺口，另记，不在本切片扩大。

实测：UnitTests 195，App.Tests 5，告警 129 持平。

### S1-4 ROI 保存（W-23 后半）DONE（代码/测试/UI PASS）

`RoiEditorViewModel`：

- 构造函数改为 `(CoordinateMapper, RecipeConfig, IRecipeStore, IDialogService)`；
- 删除 `AppPaths`、`System.IO`、`System.Text.Json`；
- `SaveToFileAsync`：
  1. 从 `Rois` 生成新的 `List<RoiData>`；
  2. 复制 `RecipeConfig`，只替换 `Rois`；
  3. `await _store.SaveAsync(copy)`；
  4. 成功后才 `_recipe.Rois = newRois`，再发 `RoisUpdatedMessage`，再 `ShowInfoAsync`；
  5. 失败只 `ShowErrorAsync`，不修改内存配方。

App.Tests：

- 保存成功 → store 收到新 ROI、`_recipe.Rois` 更新、成功提示；
- 保存失败 → `_recipe.Rois` 不变、错误提示、异常不逃逸。

验收：`ViewModels` 下搜不到 `File.WriteAllText`；保存成功/失败实际走查。
实测：UnitTests 195，App.Tests 7，告警 127（删掉 RoiEditor 的 CA1869 后自动下调）。
UI 走查：**PASS**
- 成功：临时副本保存后文件从 1163B 变为 1481B，出现“保存成功”；
- 失败：把目标路径替换成目录后保存，出现“保存失败”，未出现成功框。
重启回读不在 S1-4 验收范围内。

### S1-5 图像双写（W-12）DONE（切 Tab 已实测；缩放/平移 NOT_RUN）

- 删除 `ImagePanelView.xaml.cs:25-29` 的 `PropertyChanged` 订阅和 `ImageDisplay.Source = ...`；
- 保留 `ImagePanelView.xaml:86` 的 `Source="{Binding CurrentImage}"`；
- 缩放、平移、鼠标坐标仍留在 code-behind，因为这些是视图自身行为；
- `ImagePanelViewModelTests` 已在 S1-1 建立，本步只做视图侧删除和实际走查。

验收：切 Tab、缩放、平移正常；图像不闪回旧帧。
实测：临时 File 模式副本中切“原图/检测”，截图像素哈希不同；切回原图后哈希恢复。
缩放/平移的鼠标输入自动化未送达，记 `NOT_RUN`，不写 PASS。
门禁：`ci.cmd` 0；UnitTests 195，App.Tests 7，告警 127 持平。

### S1-6 收口 DONE

- 跑 `ci.cmd`，记录测试总数；
- 更新 `AGENTS.md` 测试数；
- 更新 `docs/WPF规范落地记录.md`：
  - W-10、W-12、W-17、W-22、W-23 → `FIXED`；
  - 新增 S1 验证记录和证据；
- 检查 `artifacts/ci/warn-baseline.json` 是否因 CA1869 减少而自动下调；如果是，连同切片一起保留；
- `git diff --stat` 确认没有现有测试断言改动。

实测：`ci.cmd` 0；UnitTests 195，App.Tests 7，总数 202；告警 127；
`ViewModels` 无 `MessageBox`/`File.WriteAllText`，`ImagePanelView` 双写已删；
W-10/W-12/W-17/W-22/W-23 均为 `FIXED`。
残项：S1-5 缩放/平移 `NOT_RUN`；不影响 S1 总验收的切 Tab 项，但不能写成 PASS。

### S1 验收（总）

- `tests/VisionStation.UnitTests` + `tests/VisionStation.App.Tests` 全绿；
- `src/VisionStation.App/ViewModels` 下搜索不到 `MessageBox`、`File.WriteAllText`；
- `ci.cmd` 退出码 0；
- 实际启动走查：急停确认/取消、ROI 保存成功/失败、相机页重连、图像页切 Tab；
- `git diff` 没有改动现有测试断言。

---

## 4. S2：组合根、启动与关闭（P2a）

> 目标不是“把 `OnStartup` 拆小”，而是让启动顺序、失败路径、关闭顺序都可验证。

### S2.1 引入 `AppHost`（DONE）

新增 `src/VisionStation.App/Composition/AppHost.cs`（或 `AppBootstrapper`），把现在 `App.OnStartup` 里的内容拆成三段：

```csharp
public sealed class AppHost : IAsyncDisposable
{
    public void Configure();                              // 配置、路径、Serilog、配方、DI
    public Task InitializeAsync(CancellationToken ct);    // 建库、连接设备、自检、启动管家
    public void Show();                                   // 创建 MainWindow、挂 Closing、Show
    public Task StopAsync(TimeSpan timeout);              // 有序关闭
}
```

约束：

- `Configure` 不连接设备、不建库、不弹框；
- `InitializeAsync` 不碰 UI；失败写日志并抛给 `App`，由 `App` 以非 0 退出；
- `Show` 只做窗口创建与显示；
- `StopAsync` 必须可重复调用（幂等）。

落地：`AppHost` / `IAppWindow` 已接入；本次只做行为不变抽取，`OnStartup` 仍同步等待，
健康检查仍弹模态框，`ConnectAll` 仍在 DI 工厂，`StopAsync` 尚未接 `Closing`。
`ci.cmd` 通过（195 + 12 = 207，告警 119），真实进程启动/关窗/重启均退出 0。

### S2.2 调整 `App.xaml.cs`（OPEN）

- `OnStartup` 只保留：全局异常处理器、`_host.Configure()` → `await _host.InitializeAsync(ct)` →
  `_host.Show()`、失败 `Shutdown(1)`。
- 去掉 `EnsureCreatedAsync().GetAwaiter().GetResult()`（`App.xaml.cs:117`），改为 `await`（W-20）。
- 健康检查的模态框（`App.xaml.cs:109`）改为：写日志 + 状态栏/自检结果，不在启动路径弹模态框；无人值守路径尤其不能弹。
- `OnExit` 只做最终兜底：确保容器释放、`Log.CloseAndFlush()`、看门狗信号；**不要**在 UI 线程上 `.GetAwaiter().GetResult()`。

### S2.3 关闭顺序

关闭分两步：

1. `MainWindow.Closing` 由 `AppHost` 挂接：第一次关闭时 `e.Cancel = true`，`await _host.StopAsync(5s)`，然后 `Application.Current.Shutdown()`；用 `_isShuttingDown` 防重入。
2. `App.OnExit` 只处理“没走 Closing 的路径”（启动失败、系统注销、异常退出），且不等待未完成的工作。

`StopAsync` 内部顺序固定：

1. 停止接收新触发（`ITriggerSource.Stop()`）；
2. 取消应用级 `CancellationTokenSource`；
3. 等待在途帧/后台任务（有上限，例如 5 秒）；
4. 释放设备与 DI 容器；
5. `Log.CloseAndFlush()`；
6. 通知看门狗正常退出。

把这段顺序抽成 `ShutdownCoordinator`，用假对象测试调用顺序和超时，不要靠读代码确认。

### S2.4 `DeviceManager` 不在 DI 工厂里连接

`ServiceCollectionExtensions.cs:69-76` 现在在工厂里 `dm.ConnectAll()`，导致“解析即连接”。改为：

- 工厂只 `Register`，不 `ConnectAll`；
- `AppHost.InitializeAsync` 里显式调用一次 `ConnectAll()`；
- 连接失败写日志，不阻塞 UI。

### S2.5 设备构造副作用（W-01/W-02）单独切片

`HikCamera` / `WebcamCamera` / `FileCamera` 的构造函数当前会打开设备或扫文件（`HikCamera.cs:71-75`、`WebcamCamera.cs:30-35`、`FileCamera.cs:57-66`）。这属于 Devices 层契约变更：

- 现有 `DeviceLayerTests` 有“构造后即可用”的断言（如 `FileCamera_NonLoopExhausted_BecomesUnavailable`）；
- 改成“构造只赋值、`open()` 才连接”会改变测试前提，必须单独评审，不能混在 WPF 切片里顺手改。

**S2 只做 App 侧一次 `ConnectAll`，不碰设备构造函数。** 如果仍出现双重初始化，记录为 W-02 残留，等设备切片处理。

### S2 验收

- 启动失败（配方损坏/数据库不可写）→ 日志有明确原因，进程非 0 退出，不弹模态框；
- 运行中关窗 → 5 秒内退出，相机/数据库/日志释放，再次启动不报占用；
- 自动运行中关窗 → 不出现“已释放对象仍被访问”的异常；
- `ShutdownCoordinator` 测试覆盖：顺序、超时、重复调用；
- `ci.cmd` 退出码 0；
- UI 走查记录（启动、关窗、重启）写入落地记录。

---

## 5. S3：Shell、导航、View/VM 边界（P2b）

> 这是 diff 最大的一片。拆成两个可独立编译的提交：S3a 导航骨架，S3b 去 View DI。

### S3.1 目标形态

```text
MainWindow (View, 无业务依赖)
 └─ DataContext = ShellViewModel
     ├─ Sidebar
     ├─ StatusBar
     └─ CurrentPage  ← INavigationService
          ├─ InspectionPageViewModel → InspectionPageView
          │    ├─ ImagePanelViewModel
          │    ├─ ResultPanelViewModel
          │    ├─ LogPanelViewModel
          │    ├─ ControlPanelViewModel
          │    └─ RoiEditorViewModel（覆盖层）
          └─ CameraPageViewModel → CameraPageView
```

- `MainWindow` 不再注入 8 个 View（`MainWindow.xaml.cs:15-44`）。
- 页面切换走 `INavigationService`，不再用 `NavigateMessage` + `Visibility`（`SidebarViewModel.cs:49-53`、`MainWindow.xaml.cs:46-54`）。
- View 不进 DI；页面 VM 默认 Transient；Shell/StatusBar 等跨页状态可以是 Singleton。
- `IServiceProvider` 只允许出现在组合根和 `IPageFactory` 实现里，VM 里不允许 `GetRequiredService`。

### S3.2 S3a：导航骨架

1. 新增 `INavigationService` / `NavigationService`：
   - `Navigate(string route)`；
   - `object? CurrentPage`；
   - `string CurrentRoute`；
   - 未知路由明确失败（抛异常或返回 false），不要静默什么都不做。
2. 新增 `IPageFactory`，只在这里用 `IServiceProvider` 创建页面 VM：
   - `"检测"` → `InspectionPageViewModel`；
   - `"相机"` → `CameraPageViewModel`；
   - 其余路由：**先禁用**或显示占位页，行为要由人确认，不能假装能导航。
3. `SidebarViewModel` 改成调用 `INavigationService.Navigate(route)`，`ActivePage` 从导航状态推导；删除 `NavigateMessage`（`Messages.cs:68-75`）。
4. `MainWindow.xaml` 改成：

```xml
<ContentControl Grid.Column="0" Content="{Binding Sidebar}" />
<ContentControl Grid.Row="0" Content="{Binding CurrentPage}" />
<ContentControl Grid.Row="1" Content="{Binding StatusBar}" />
```

5. `MainWindow.xaml.cs` 只保留 `InitializeComponent()` 和 `DataContext`；删除 `Visibility` 切换、`NavigateMessage` 订阅、标题改写。
6. F2 改成命令：`InspectionPageViewModel.ToggleRoiEditorCommand`，在 XAML 用 `KeyBinding`；`IsRoiEditorOpen` 通过 `BooleanToVisibilityConverter` 控制覆盖层。

### S3.3 S3b：页面组合与去 View DI

1. 新增 `InspectionPageViewModel`，持有 `ImagePanel` / `ResultPanel` / `LogPanel` / `ControlPanel` / `RoiEditor` 子 VM 和 `IsRoiEditorOpen`。
   - 子 VM 由页面工厂创建；**子 VM 之间不互相引用**，跨页 VM 更不允许。
   - 父页面 VM 可以持有子 VM（组合关系），这不是“跨页通信”；跨页控制流仍走导航服务。
2. 新增 `InspectionPageView.xaml`，用 `ContentControl Content="{Binding ImagePanel}"` 等把版面搬过去；`MainWindow` 不再知道这些面板。
3. 所有 View 改成参数无参构造：
   - `ControlPanelView`、`CameraPageView`、`SidebarView`、`ImagePanelView`、`RoiEditorView` 等删除 `DataContext = vm`；
   - `RoiEditorView` / `ImagePanelView` 需要 VM 时从 `DataContext` 取，不在构造函数注入；
   - 加 `d:DataContext="{d:DesignInstance ...}"`，保证设计器可预览（W-24）。
4. `App.xaml` 加 DataTemplate：

```xml
<DataTemplate DataType="{x:Type vm:InspectionPageViewModel}">
    <views:InspectionPageView />
</DataTemplate>
<DataTemplate DataType="{x:Type vm:CameraPageViewModel}">
    <views:CameraPageView />
</DataTemplate>
```

5. DI 注册调整：
   - 删除 `AddViews` 里所有 `View` / `MainWindow` 注册；
   - 页面 VM 用 `AddTransient`；
   - Shell / StatusBar / 服务用 `AddSingleton`；
   - `MainWindow` 由 `AppHost.Show()` 直接 `new`，不进容器。
6. 设计时：每个页面至少有一个设计时 VM 或 `d:DataContext`，设计器不能空白。

### S3.4 S3 测试

- `NavigationServiceTests`：路由 → 预期 VM 类型；未知路由行为；重复导航是否复用/新建符合声明。
- `SidebarViewModelTests`：点击导航项 → `Navigate` 被调用、`ActivePage` 更新。
- `AppCompositionTests`：
  - 容器里没有 `Window` / `UserControl` / `View` 类型；
  - 页面 VM 是 Transient；
  - 关键接口有实现。
- 不写 `MainWindow` 单元测试；窗口行为靠实际走查。

### S3 验收

- 连续切页 20 次，内存/句柄不持续增长；
- 离开页面后后台刷新停止（当前只有相机页，先确认没有常驻定时器）；
- View 不在 DI；`MainWindow` 构造参数只剩 VM/服务；
- 代码里搜不到 `NavigateMessage` 和用 `Visibility` 切页面；
- F2 编辑 ROI 正常，切页自动退出编辑；
- 设计器能预览至少“检测”“相机”两页；
- `ci.cmd` 0 + 实际启动走查。

---

## 6. S4：线程、工作流与日志（P3）

> 工业软件最硬的一条：UI 线程只做 UI。当前整条检测链路在 UI 线程（`MainViewModel.cs:132-235`）。

### S4.1 抽出 `IInspectionWorkflow`

目标：把“采图 → Pipeline → 标注 → 存图 → 存库”从 VM 移走。

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

- 工作流内部用 `Task.Run` 或全异步 API，不访问 `Dispatcher`；
- `BitmapSource.Create` 后立即 `Freeze()`，再返回；
- `Mat` 的所有权在方法内结束，不跨线程共享；
- 工作流不更新 UI 状态，只返回结果。
- `IInspectionWorkflow` 放在 **App 层**（`src/VisionStation.App/Services`），它是呈现编排；
  真正的采图/判定/持久化如果下沉到 `VisionStation.Service`，Service 仍不得引用 WPF。
  位图转换可以放在 App 的 `IBitmapAdapter` 里，在后台线程创建并 `Freeze()`。

### S4.2 `InspectionPageViewModel` 只做编排

- 订阅 `ITriggerSource.Triggered`，用 `async void` 事件处理器包住全部异常；
- 防重入用 `Interlocked.CompareExchange` 或 `SemaphoreSlim(1,1)`，复位放 `finally`；
- `await _workflow.RunOnceAsync(_cts.Token)`；
- 结果通过 `InspectionResultMessage` 广播（通知，不是控制流），不要直接写子 VM 属性；
- 删除 `Application.Current.Dispatcher.CheckAccess()` / `InvokeAsync`（`MainViewModel.cs:132-138`）；
- 删除 `_imagePanel` / `_statusBar` 字段（`MainViewModel.cs:49-51`），改由各面板订阅消息。

### S4.3 面板改为订阅者

- `ImagePanelViewModel` 订阅 `InspectionResultMessage`，更新 `OriginalImage` / `AnnotatedImage`；消息里带已冻结的位图。
- `StatusBarViewModel` 订阅 `InspectionResultMessage`，更新 OK/NG/Cycle/FPS；不再由 `MainViewModel` 直接赋值。
- `ResultPanelViewModel` 改成数据驱动：
  - 遍历 `report.Results`，不要写死“特征有无/圆孔直径/划痕/缺口”四行（`ResultPanelViewModel.cs:50-79`）；
  - VM 只暴露 `IsOk` / `StatusText` / `Value` 等语义，颜色由 XAML `DataTrigger` 决定；
  - 删除 VM 里的十六进制颜色（`ResultPanelViewModel.cs:36,83,103`）。
- 本片引入 `IUiDispatcher`（S1 没有消费者，所以推迟到这里）。
- `LogPanelViewModel`：
  - 用 `IUiDispatcher.InvokeAsync` 替代 `Dispatcher.Invoke`（`LogPanelViewModel.cs:53`）；
  - `Entries.Refresh()` 做节流/合批（例如 100–250 ms 一次），不要每条日志刷新；
  - 后续改成 Serilog `UiLogSink`（有界队列），消除 `LogMessage` 与 Serilog 双轨（W-13）。

### S4.4 测试

- `InspectionWorkflowTests`：
  - 正常帧 → 返回报告、图片、耗时；
  - 采图为 null → 返回失败/跳过，不抛；
  - Pipeline 抛异常 → 被捕获并上报；
  - 取消令牌 → `OperationCanceledException` 可预期，不吞掉。
- `InspectionPageViewModelTests`：
  - 两个并发触发 → 只跑一次；
  - 处理中关闭 → 取消并等待，不更新已释放对象；
  - 异常后防重入标志复位。
- 位图测试：`Assert.True(result.OriginalImage!.IsFrozen)`。
- `ResultPanelViewModelTests`：2 个工具、5 个工具的配方都能按报告生成行，不依赖固定工具名。

### S4 验收

- 自动运行 5 分钟，界面可拖动/切页，无连续 100 ms 以上卡顿；
- 检测/存库期间 UI 线程没有 `Pipeline.Run` / 文件 IO / DB；
- 运行中关窗不抛“跨线程访问已释放对象”；
- `src/VisionStation.App/ViewModels` 下搜不到 `Application.Current` / `Dispatcher`；
- `ci.cmd` 0 + 实际启动走查。

---

## 7. S5：配置与路径（P4）

> 这一片有现场部署决策，先问人，不要按“常见值”改。

### S5.1 强类型 Options

在 `AppHost.Configure` 里绑定并校验：

- `CameraOptions`（Mode、ImageFolder、FileFilter、Loop）；
- `TriggerOptions`（Mode、TimerIntervalMs）；
- `StorageOptions`（Mode、ImageFolder、DbPath）；
- `HousekeepingOptions`（现有参数）；
- `LoggingOptions`（LogFolder、保留数量）；
- `RecipeOptions`（Path）。

校验失败在启动期写明确日志并非 0 退出，不静默回落默认值。业务参数（保留天数、容量上限）仍按 AGENTS 第 4 节问人。

### S5.2 去掉散落的 `IConfiguration`

- `CameraFactory.cs:29,43,44` 改为接收 `CameraOptions`；
- `AppPaths.cs:41-46` 改为接收 `PathOptions` / 明确路径；
- `ServiceCollectionExtensions.cs:62,67,93,132-137` 改为从 Options 取值；
- `reloadOnChange`（`App.xaml.cs:67`）要么接 `IOptionsMonitor` 并让消费方响应，要么关掉；没有消费者就关掉（W-14）。
- `Trigger:Mode`（`appsettings.json:9`）当前无人读取：删除或接通，先问人，不能顺手改行为。

### S5.3 可写路径（W-15，需决策）

当前 `AppPaths` 基于 `Environment.CurrentDirectory`（`AppPaths.cs:63-80`），快捷方式/计划任务启动时路径不可控。

需要人拍板：

- 方案 A（推荐）：可写数据放 `%LocalAppData%\VisionStation`，安装资源基于 `AppContext.BaseDirectory`；
- 方案 B：保留便携模式，但必须显式声明（启动参数/配置项），不能靠 cwd 猜；
- 现有 `visionstation.db`、`Logs/`、`InspectedImages/` 的迁移/兼容策略。

未拍板前只做 Options 重构，不改默认落盘位置。

### S5 验收

- 从不同 cwd 启动（快捷方式/命令行）写入目录符合决策；
- 配置缺项/非法 → 启动失败信息可读；
- 没有 VM/Service 直接依赖 `IConfiguration`；
- `ci.cmd` 0 + 启动走查。

---

## 8. S6：资源、命名、注释（P5）

可以穿插，但不要在 S3/S4 同时改命名和结构，否则 diff 无法审查。

- `App.xaml:6-114` 拆成 `Resources/Theme.xaml`、`Controls.xaml`、`Converters.xaml`；
- 去掉 View 里的硬编码颜色（如 `SidebarView.xaml:4`、`CameraPageView.xaml:4-5`），改用命名资源；
- Converter 只注册一次：`App.xaml:20` 与 `StatusBarView.xaml:7` 的重复注册合并（W-16）；
- `ICamera.open/close` → `Open/Close`（`ICamera.cs:32,35`）单独切片，涉及 Devices 与测试，不和 S3 混；
- 业务代码里的 `修复（F-...）`/事故编号移到 `docs/` 或 `audit/`，代码只留“为什么”（W-19）；
- 每个页面补设计时数据，保证 XAML 设计器不是空白。

**验收**：View 中无新增硬编码色值；资源键无重复；`ci.cmd` 0；实际启动走查主题/日志面板。

---

## 9. 偏差到切片的映射

| 偏差 | 切片 | 说明 |
|---|---|---|
| W-01 | S1/S2/S3 + 设备切片 | App VM 构造订阅 → S1/S3/S4；`DeviceManager.ConnectAll` → S2；相机构造函数 → 单独设备切片 |
| W-02 | 设备切片 | `HikCamera` 构造 + `open()` 双重初始化，需改设备契约与测试前提 |
| W-03 | S3 | View 退出 DI、DataTemplate |
| W-04 | S3 | `INavigationService` 替代 Messenger + Visibility |
| W-05 | S3 | `MainWindow` code-behind 只留视图行为 |
| W-06 | S4 | 工作流下沉 |
| W-07 | S4 | 检测/存库移出 UI 线程 |
| W-08 | S4 | `MatToBitmap` 后 `Freeze()` |
| W-09 | S4 | `Dispatcher.Invoke` → `InvokeAsync` + 节流 |
| W-10 | S1 | `IDialogService` |
| W-11 | S4 | 结果面板数据驱动、去 VM 颜色 |
| W-12 | S1 | 删除图像双写 |
| W-13 | S4 | Serilog UI sink |
| W-14 | S5 | Options、热重载、`Trigger:Mode` |
| W-15 | S5 | 可写路径（需决策） |
| W-16 | S6 | 资源字典、颜色、Converter |
| W-17 | S1 | App 测试工程 |
| W-18 | S6/设备切片 | `Open/Close` 命名 |
| W-19 | S6 | 事故编号移出业务代码 |
| W-20 | S2 | 异步建库 |
| W-21 | S2 | 应用级取消 + 有序关闭 |
| W-22 | S1 | 重连走 `ICamera.open()` |
| W-23 | S1 | `IRecipeStore` + `IDialogService` |
| W-24 | S3 | 无参构造 + 设计时 DataContext |

---

## 10. 每片交付模板

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

没有“基本通过”“应该没问题”。做不到的写 `NOT_RUN` 并说明缺什么。

---

## 11. 停止线（遇到就停，不要自行决定）

1. 需要改现有测试断言才能过；
2. 需要改判定阈值、保留天数、容量上限、急停确认策略；
3. 需要决定导航选型、未实现页面行为、页面缓存策略；
4. 需要决定可写数据路径或旧数据迁移；
5. 需要改 `HikCamera`/`FileCamera` 构造函数契约（会动设备测试前提）；
6. UI 无法实际启动走查，却想判“完成”；
7. 一个切片超过约 10 个文件或 400 行 diff，却不想拆。

---

## 12. 当前执行点

S1 内部按 S1-1 → S1-6 顺序做：

- **S1-1 DONE**：App.Tests + CI 接线；`ci.cmd` 0，129 告警持平，192 + 2 测试。
- **S1-2 DONE**：`IRecipeStore` + `JsonRecipeStore`（Service）+ 3 个 UnitTests；当前 195 + 2 测试。
- **S1-3 DONE**：`IDialogService` + `WpfDialogService` + ControlPanel（W-10 前半、W-22）；UI 急停取消/确认/重连 PASS。
- **S1-4 DONE**：RoiEditor 保存（W-23 后半、W-10 剩余）；UI 保存成功/失败 PASS；当前 195 + 7 测试，告警 127。
- **S1-5 DONE（残项）**：ImagePanelView 双写（W-12）；切 Tab PASS，缩放/平移 `NOT_RUN`。
- **S1-6 DONE**：收口完成；W-10/W-12/W-17/W-22/W-23 `FIXED`。
- **S2.1 DONE**：`AppHost` + `IAppWindow` + 5 个 AppHostTests；195 + 12 = 207，告警 119；
  真实进程启动/关窗/重启退出 0。仍保留同步等待、健康模态框、DI 工厂 `ConnectAll`。
- 下一步 S2.2：去 W-20 同步等待、去启动模态框、启动失败非 0 退出。

不要提前动 `MainWindow`、导航、线程或设备构造函数。S1 只建立“以后每改一处都有测试和证据”的护栏；
护栏立住之后，再进入 S2 的组合根与启停。
