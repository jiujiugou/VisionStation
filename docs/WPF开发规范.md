# WPF 开发规范

> **这份文件只写规则。** 本项目当前哪里没做到、迁移到哪一步、有哪些未清偿项，一律写在
> `docs/WPF规范落地记录.md`，两份文件不要互相掺内容。
>
> 适用范围：`src/VisionStation.App` 以及任何引入 WPF 类型（`System.Windows*`、`Dispatcher`、
> `BitmapSource`、`MessageBox`）的代码。Core / Devices / Storage / Service 仍按 AGENTS.md 的
> 依赖方向执行；本文不改变那四层的边界。
>
> 用词：**必须（MUST）** = 违反即返工；**应该（SHOULD）** = 偏离要在 PR/提交说明里写理由；
> **可以（MAY）** = 不强制。

---

## 0. 先建立正确的心智模型

WPF 不是“没有 HTTP 的 ASP.NET Core”。把服务端习惯直接搬过来，会得到一堆能编译、但生命周期和
线程都不对的代码。

| ASP.NET Core 习惯 | WPF 的现实 | 直接照搬的后果 |
|---|---|---|
| 请求作用域，每次请求新建对象图 | 进程/窗口/页面各有生命周期，没有 request scope | 单例泛滥、状态互相污染、对象永不释放 |
| Controller 无状态，DI 解析即调用 | 可视树由 WPF 创建，UI 有线程亲和性 | View 被注册成服务、构造函数里连接硬件 |
| 中间件管线决定流程 | 绑定、命令、路由事件、可视树决定交互 | 用 Messenger 当路由，页面状态两套真相 |
| `IHostedService` 由 Host 管启停 | 启停必须自己接到 `OnStartup`/`OnExit` | 后台任务没有取消令牌，关窗时还在写库 |
| 配置热重载是常态 | 桌面配置通常只读，用户设置要单独持久化 | 开了 `reloadOnChange` 却没人消费 |
| 服务端没有 UI 线程 | 只有一个 UI 线程，阻塞它就是整机卡死 | OpenCV/DB 跑在 UI 线程，界面假死 |

**五条红线**（后文展开）：

1. 构造函数不产生副作用；
2. View 不进 DI，VM 不引用 View；
3. VM 不碰 `Application.Current` / `MessageBox` / `Dispatcher` / `Brush` / `Visibility`；
4. UI 线程只做 UI，重活下后台，位图必须 `Freeze()`；
5. 导航走导航服务，不用 Messenger + `Visibility` 拼页面。

---

## 1. 组合根与进程生命周期（WPF-L）

### WPF-L1.1 唯一组合根
`App.OnStartup` 是**唯一**允许创建服务、读配置、组装对象图的地方。ViewModel、View、Service
内部不得 `new` 基础设施（`DbContext`、`HttpClient`、相机、Repository、日志器）。

### WPF-L1.2 构造函数不做事
构造函数**必须**只做赋值和自身参数的合法性校验（纯计算）。以下行为一律禁止放在构造函数：

- 连接/打开设备、启动线程、`Task.Run`；
- 读写文件、数据库、配置；
- 订阅事件或 Messenger；
- 发消息、弹窗、更新其他对象状态；
- 启动定时器。

需要初始化的对象**必须**提供显式的 `InitializeAsync(CancellationToken)` / `StartAsync`，
由组合根按顺序调用。纯值对象（Options、参数校验）在构造函数里抛异常是正确的。

### WPF-L1.3 启动分三段
启动**必须**是可见的三段，而不是一个 200 行的 `OnStartup`：

1. **Configure**：读配置、建容器、绑定 Options、注册类型；
2. **InitializeAsync**：建库/迁移、打开设备、启动后台服务、自检；
3. **Show**：创建并显示主窗口。

初始化失败**必须**留痕并以非 0 退出码结束；无人值守路径不得弹模态框。

### WPF-L1.4 关闭是有顺序的
`OnExit`（以及窗口关闭触发的路径）**必须**按下面顺序收尾，且每一步都要有超时上界：

1. 停止接收新触发；
2. 取消应用级 `CancellationTokenSource`；
3. 等待在途帧/后台任务结束（有上限，例如 5 秒）；
4. 释放设备与 DI 容器；
5. `Log.CloseAndFlush()`；
6. 通知看门狗这是正常退出。

“先 Dispose 容器、再取消在途任务”是错误顺序：任务会落在已释放的上下文/句柄上。

### WPF-L1.5 所有长任务都要有取消令牌
后台循环、定时器回调、采集线程、清理任务**必须**接收并检查 `CancellationToken`。
禁止只靠 `bool _running` + `Thread.Sleep` 实现可取消等待。

### WPF-L1.6 关窗必须可验证
任何涉及采集/检测/存库的改动，交付前**必须**实际测试：运行中直接关窗，进程应在约定时间内退出，
相机句柄、数据库连接、日志文件都正常释放；再次启动不报“设备被占用”。

---

## 2. DI 与对象生命周期（WPF-D）

### WPF-D1.1 生命周期表

| 生命周期 | 允许注册的东西 | 禁止注册的东西 |
|---|---|---|
| Singleton | 配置/Options、日志、Repository、图片存储、应用服务、相机会话所有者、触发源 | 页面 VM、窗口、对话框、View |
| Transient | 页面 ViewModel、对话框 ViewModel、短期命令对象 | 进程级状态、设备句柄 |
| Scoped | **不使用**（WPF 没有请求边界，不要为了“像 Web”而引入） | — |

### WPF-D1.2 View 不是服务
View（`Window`/`UserControl`）**必须**由 WPF 创建（`DataTemplate`、窗口工厂、`ContentControl`），
不得注册进 DI，也不得作为构造函数参数注入到别的 View/VM。

### WPF-D1.3 页面 VM 默认 Transient
页面 ViewModel 的生命周期**必须**跟页面一致。需要缓存页面时，由导航服务显式声明“缓存”，
不能靠“注册成单例”顺手实现。

### WPF-D1.4 禁止服务定位器
除组合根和工厂外，代码中**不得**出现 `IServiceProvider` / `GetRequiredService` /
`Application.Current.Services`。需要运行时创建对象，用工厂接口（如 `ICameraFactory`）注入。

### WPF-D1.5 释放责任写清楚
- 容器负责释放它创建的 Singleton；
- ViewModel 负责取消自己的订阅、取消令牌、定时器；
- View 负责释放自己创建的可视/输入资源；
- 接口返回可释放对象时，所有权必须在接口注释里写明（谁创建、谁释放）。

### WPF-D1.6 注册关系要可测
应该为 App 层提供一组 DI 装配测试：断言 View 类型未注册、生命周期符合上表、
页面 VM 是 Transient、关键接口有实现。装配错误必须在测试里暴露，而不是运行时才发现。

---

## 3. 分层与职责（WPF-A）

### WPF-A1.1 App 只做两件事
`VisionStation.App` = **呈现层 + 组合根**。不得在 App 里做检测判定、阈值比较、文件格式决策、
数据保留策略。这些属于 `Core` / `Service` / `Storage`。

### WPF-A1.2 ViewModel 只做呈现
ViewModel **必须**只包含：

- 可绑定状态（属性、集合、选中项）；
- 命令（`ICommand` / `[RelayCommand]`）；
- 输入校验与错误提示状态；
- 调用应用服务并暴露 `IsBusy` / 错误信息。

ViewModel **不得**直接：跑 OpenCV、写 SQL、拼文件路径、开关设备、决定判定阈值。

### WPF-A1.3 工作流下沉到应用服务
“采图 → 检测 → 标注 → 存图 → 存库 → 通知”是一条应用工作流，**必须**放在 Service/应用服务里，
ViewModel 只负责发起（命令）和展示（状态）。工作流服务接受 `CancellationToken` 并返回结果对象，
不直接碰 UI 类型。纯呈现适配（如 `Mat` → `BitmapSource`）属于 App 层，不得因此把 WPF 类型带进
Service/Core。

### WPF-A1.4 VM 之间不互相引用
ViewModel **不得跨页面**构造函数注入另一个 ViewModel。页面内部的父子组合由页面工厂创建，父 VM
可以持有子 VM，但子 VM 之间不得互相引用。跨页面通信优先用：
共享应用服务 > Messenger 通知 > 事件。**控制流（导航、启动/停止检测）不得用 Messenger 表达。**

### WPF-A1.5 Messenger 只做通知
`WeakReferenceMessenger` 用于“某事发生了”的广播，不用于“请执行某动作”。消息**必须**是不可变的
通知对象；订阅者**不得**在消息处理器里做长耗时操作。

### WPF-A1.6 可测性是硬约束
ViewModel **必须**能在普通线程（非 STA、无 `Application` 实例）构造并测试。
任何让 VM 依赖 `Application.Current` 的设计都视为违规。

---

## 4. View / ViewModel 契约（WPF-V）

### WPF-V1.1 ViewModel-first
页面通过 `ContentControl Content="{Binding CurrentPage}"` + `DataTemplate` 映射到 View。
View 不使用构造函数注入业务依赖；`DataContext` 由导航服务/模板提供。

### WPF-V1.2 View 必须可设计时实例化
View **必须**有参数无参构造函数（`InitializeComponent` 之外不做事），并提供
`d:DataContext="{d:DesignInstance ...}"` 或设计时 VM，保证 XAML 设计器不是空白。

### WPF-V1.3 code-behind 只允许视图自身行为
允许：焦点、输入/鼠标捕获、拖拽、动画、DPI/尺寸测量、纯渲染辅助。
禁止：访问 Repository/相机/Pipeline、导航、弹业务对话框、拼业务文案、改其他 VM 状态。

### WPF-V1.4 不要手写绑定的替代品
XAML 已经能表达的事情（`Source`、`Visibility`、`SelectedItem`、`IsEnabled`）**必须**用绑定/触发器，
不得在 code-behind 里订阅 `PropertyChanged` 再手动赋值控件属性。

### WPF-V1.5 VM 输出语义，不输出外观
ViewModel **必须**暴露 `IsOk`、`Status`、`LogLevel`、`IsBusy` 这类语义状态；
颜色、画刷、字体、`Visibility` 字符串**不得**出现在 VM。外观由 Style / DataTrigger / Converter 决定。

### WPF-V1.6 命令要有可用性
会失败或依赖前置条件的命令**必须**通过 `CanExecute` / `IsBusy` 禁用，不能点了没反应或重复提交。
异步命令**必须**防重入（执行期间禁用自身或加守卫）。

---

## 5. 导航（WPF-N）

### WPF-N1.1 一个导航服务
页面切换**必须**通过 `INavigationService`（或等价的导航状态对象）完成：设置 `CurrentPage`、
处理参数、声明页面缓存策略。禁止在 `MainWindow` 里用 `Visibility` 切换页面。

### WPF-N1.2 导航不是消息
禁止用 `NavigateMessage` + 视图可见性实现路由。导航是控制流，必须有明确调用点和可测试的导航服务。

### WPF-N1.3 页面生命周期写清楚
每个页面**必须**声明是“每次新建”还是“缓存”。缓存页面要在离开时暂停后台刷新、回来时恢复，
不能把定时器一直挂在后台。

### WPF-N1.4 对话框走服务
确认框、错误框、文件选择**必须**通过 `IDialogService`（或 `IFileDialogService`）。
ViewModel 只表达“需要确认/需要选择”，不直接调用 `MessageBox` / `OpenFileDialog`。

---

## 6. 绑定、集合与校验（WPF-B）

### WPF-B1.1 用标准通知机制
ViewModel 继承 `ObservableObject`（CommunityToolkit.Mvvm）；集合用 `ObservableCollection<T>`；
过滤/排序用 `ICollectionView`。不要自造通知机制。

### WPF-B1.2 集合只在 UI 线程改
`ObservableCollection` 的增删**必须**在 UI 线程执行。后台线程产生数据时，用
`Dispatcher.InvokeAsync` 或先写入不可变快照再整体替换。

### WPF-B1.3 刷新要节流
`ICollectionView.Refresh()`、日志追加、状态栏更新这类高频操作**必须**节流/合批（例如 4–10 Hz），
不能每帧/每条日志都刷新整个视图。

### WPF-B1.4 格式化留在 View
VM 暴露数值、枚举、时间等原始类型；显示格式（小数位、单位、颜色）由 XAML/Converter 负责。
Converter **必须**是纯函数，无 IO、无状态、无异常逃逸。

### WPF-B1.5 校验要可反馈
输入校验**必须**通过 `INotifyDataErrorInfo` 或明确的校验结果暴露给 View，并禁用提交命令；
不得只写日志或静默回落默认值。

### WPF-B1.6 禁止绑定全局静态状态
不得把 `Application.Current`、静态服务、静态集合直接绑到 UI。所有可绑定状态来自注入的 VM/服务。

---

## 7. 线程模型（WPF-T）—— 工业软件最硬的一条

### WPF-T1.1 UI 线程只做 UI
UI 线程上**不得**执行：OpenCV 运算、模板匹配、ONNX 推理、数据库读写、文件/网络 IO、
等待设备、`Thread.Sleep`、`.Result` / `.Wait()` / `GetAwaiter().GetResult()`。

判据：任何一次用户操作导致界面连续无响应超过 100 ms，都按缺陷处理。

### WPF-T1.2 重活下后台，结果回 UI
耗时工作**必须**通过 `Task.Run` 或应用服务的异步 API 执行；结果回到 UI 线程后再更新可绑定状态。
应用服务内部不访问 `Dispatcher`。

### WPF-T1.3 跨线程位图必须冻结
`BitmapSource` **必须**在后台线程创建后立即 `Freeze()`，再交给 UI；否则跨线程访问会抛
`InvalidOperationException` 或造成渲染异常。OpenCV `Mat` 不得跨线程共享，除非有明确的所有权/锁约定。

### WPF-T1.4 ConfigureAwait 约定
- ViewModel / View 层：`await` 默认 `ConfigureAwait(true)`（需要回到 UI 上下文）；
- 基础设施 / 应用服务：`ConfigureAwait(false)`；
- 不得在 UI 线程同步等待异步方法。

### WPF-T1.5 Dispatcher 用法
跨线程更新 UI 用 `Dispatcher.InvokeAsync`，不用阻塞的 `Dispatcher.Invoke`。
能用绑定解决的，不要直接碰 Dispatcher。

### WPF-T1.6 后台异常就地拦截
线程池/Timer/采集线程回调**必须** try/catch 并上报；未捕获异常会终止进程。
`finally` 里必须归还资源（相机缓冲、锁、标志位）。
`async void` 只允许出现在事件处理器，且必须包住全部异常。

### WPF-T1.7 防重入
触发/命令/定时器**必须**有防重入守卫，并在 UI 上体现“处理中”（禁用按钮、显示忙状态）。
守卫复位必须放在 `finally`，不能只在成功路径复位。

### WPF-T1.8 关闭时取消并等待
关闭流程**必须**取消在途工作并等待有界时间；不得让后台任务在容器释放后继续访问服务。

---

## 8. 资源、样式与设计时（WPF-R）

### WPF-R1.1 资源字典分类
样式与画刷**必须**按职责拆分（例如 `Resources/Theme.xaml`、`Resources/Controls.xaml`、
`Resources/Converters.xaml`），在 `App.xaml` 用 `MergedDictionaries` 合并。不要把所有东西堆在一个文件里。

### WPF-R1.2 视图不写死颜色
View 中**不得**出现十六进制颜色/魔法画刷值；必须引用命名资源（主题色、状态色）。
例外只允许设计时预览，且要注释说明。

### WPF-R1.3 转换器只注册一次
Converter 资源**必须**在全局资源字典注册一次；不得在多个 View 里重复 `x:Key` 同名注册。
Converter 只做显示转换，不承载业务判断。

### WPF-R1.4 设计时数据
每个页面**应该**提供设计时 VM/示例数据，保证 XAML 设计器可预览；设计时数据不得连接真实设备。

### WPF-R1.5 样式优先于内联
重复出现两次以上的控件外观**必须**提取为 Style；隐式 Style 只在明确要影响整个应用时使用。

---

## 9. 配置、路径与用户数据（WPF-C）

### WPF-C1.1 组合根读一次
配置**必须**在组合根读取并绑定为强类型 Options（record/class），再注入使用。
ViewModel / View / 应用服务**不得**直接依赖 `IConfiguration`。

### WPF-C1.2 安装资源与可写数据分开
- 安装资源（模板、配方、模型、只读数据）：放安装目录，基于 `AppContext.BaseDirectory` 解析；
- 可写数据（数据库、日志、检测图片、用户设置）：放用户可写目录，默认
  `%LocalAppData%\VisionStation`，除非部署方式明确要求便携模式。

**禁止**把运行期数据写到 `Program Files` 或安装目录（权限/虚拟化/升级覆盖）。

### WPF-C1.3 热重载要么接通要么关掉
开启 `reloadOnChange` **必须**同时有 `IOptionsMonitor` 或等价机制让消费方真正响应；
没有消费者就关掉，避免“看起来支持热更新、实际不会变”。

### WPF-C1.4 启动期校验，失败要可读
配置缺失/非法**必须**在启动期以明确消息失败（写日志 + 退出），不得静默回落默认值。
业务参数（判定阈值、保留天数、容量上限）**不得**由开发/AI 自行决定，按 AGENTS.md 第 4 节执行。

### WPF-C1.5 不把秘密写进配置文件
密码、令牌、私钥**不得**出现在 `appsettings.json` 或代码仓库。需要时用 Windows 凭据管理器/
受保护存储，并在 ADR 里记录方案。

---

## 10. 日志、异常与对话框（WPF-E）

### WPF-E1.1 单一日志管线
Serilog 是唯一日志管线。UI 日志面板**必须**做成 Serilog sink（有界缓冲 + 线程安全投递），
不得让业务代码同时发 `LogMessage` 又写 `ILogger` 形成两套记录。

### WPF-E1.2 对话框只在 UI 层
`MessageBox` / `OpenFileDialog` 只允许出现在 View 或 `IDialogService` 实现里。
ViewModel、应用服务、设备层不得弹框。

### WPF-E1.3 无人值守不阻塞
自动运行、后台清理、启动自检的失败路径**不得**弹模态框。必须写日志 + 更新状态灯/状态栏，
由操作员或看门狗处理。

### WPF-E1.4 全局异常处理不阻塞
`DispatcherUnhandledException` / `TaskScheduler.UnobservedTaskException` /
`AppDomain.UnhandledException` 只做留痕和最小清理，不得弹窗、不得等待、不得访问 Dispatcher。

### WPF-E1.5 失败必须可观测
禁止空 `catch`。允许降级的路径必须记录“降级了什么、为什么、影响范围”，并在 UI 上给出可反证的状态。

---

## 11. 测试策略（WPF-TS）

### WPF-TS1.1 App 层必须有测试工程
新增 `VisionStation.App.Tests`（`net8.0-windows`，引用 App）。现有
`VisionStation.UnitTests`（`net8.0`）保持不动，继续覆盖 Core/Storage/Service。

### WPF-TS1.2 VM 测试不启动 WPF
ViewModel 测试**必须**在普通线程运行，不创建 `Application`、不创建窗口。
UI 交互通过注入的 `IUiDispatcher` / `IDialogService` / `INavigationService` 替身验证。

### WPF-TS1.3 至少覆盖这些
每个页面 VM 至少测试：初始状态、命令成功路径、命令失败/禁用路径、`IsBusy` 防重入、
导航参数、错误提示。工作流服务测试取消与异常路径。

### WPF-TS1.4 现有断言不许动
按 AGENTS.md：只准加测试，不准放宽断言、抬阈值、加 `Skip`。

### WPF-TS1.5 UI 走查是人工门禁
自动化测试不能替代实际启动。见第 13 节清单。

---

## 12. 命名、文件与注释（WPF-S）

### WPF-S1.1 命名
- 方法/属性 PascalCase：`Open`、`Close`、`Capture`；异步方法 `Async` 后缀；
- 接口 `I` 前缀；View `View` 后缀；ViewModel `ViewModel` 后缀；服务 `Service` 后缀；
- 命令属性 `XxxCommand`；布尔属性用 `Is/Has/Can` 前缀。

### WPF-S1.2 文件组织
一个文件一个公开类型（小型 record/enum 可同文件）。View 与 ViewModel 目录对应；
App 内按职责分 `Views` / `ViewModels` / `Composition` / `Navigation` / `Resources` / `Infrastructure`。

### WPF-S1.3 注释写“为什么”
注释只写：不变量、失败模式、取舍原因、外部约束。**不得**把 `F-xxx` 事故编号、修复历史、
大段排查过程写进业务代码——这些属于 `docs/`。

### WPF-S1.4 null 约定
启用 nullable；`null!` 只允许在框架必然赋值的场景（如 `InitializeComponent` 后字段），
并注明理由。返回 `null` 表示失败的方法必须在 XML 注释里写清楚。

### WPF-S1.5 禁止注释掉的代码
不用的代码直接删；需要保留的历史在 git 里。

---

## 13. 做完的定义（桌面版）

一个 WPF 切片只有同时满足以下条件才算完成：

1. `ci.cmd` 退出码 0，且输出贴进交付说明；
2. `VisionStation.App.Tests`（P1 起）与现有测试全绿；
3. 实际 `dotnet run --project src/VisionStation.App` 启动并走查；
4. 无 UI 线程阻塞：连续操作/自动运行 5 分钟，界面可响应，无卡死；
5. 运行中关窗：进程按预期退出，再启动不报设备/数据库占用；
6. 资源与订阅无泄漏：重复切页 20 次，内存/句柄不持续增长；
7. `git diff --stat` 已检查，没有测试断言被改动；
8. `docs/WPF规范落地记录.md` 已更新（新增偏差或状态变更）；
9. 新增的 `F-xxx`/设计取舍写进文档，不写进业务代码注释。

### UI 走查清单

- [ ] 启动自检结果与实际设备状态一致；
- [ ] 页面切换正常，离开页面后后台刷新停止；
- [ ] 图像显示、缩放、平移、ROI 编辑正常；
- [ ] 按钮在不可用状态下确实禁用（不是点了没反应）；
- [ ] 自动运行/停止/急停路径正确，状态灯可反证；
- [ ] 相机断开/重连后 UI 状态跟随真实状态；
- [ ] 日志面板有界、可过滤、不卡顿；
- [ ] 高 DPI / 窗口缩放后布局不重叠、不越界；
- [ ] 关闭窗口无异常，下次启动正常；
- [ ] `Logs/` 与崩溃日志没有新的未处理异常。

---

## 14. 评审清单（提交前逐条打勾）

- [ ] 构造函数没有连接设备、订阅、发消息、读写文件；
- [ ] 新增 View 没有进 DI；新增 VM 是 Transient（除非有理由）；
- [ ] VM 没有 `Application.Current` / `MessageBox` / `Dispatcher` / `Brush` / `Visibility`；
- [ ] 重活不在 UI 线程；跨线程位图已 `Freeze()`；
- [ ] 后台循环有取消令牌、异常拦截、`finally` 释放；
- [ ] 导航走导航服务，没有新增 `NavigateMessage` + `Visibility`；
- [ ] 颜色/样式来自资源，没有新增硬编码色值；
- [ ] 配置在组合根读取，没有在 VM/Service 里直接读 `IConfiguration`；
- [ ] 失败路径可观测，没有空 catch、没有无人值守模态框；
- [ ] 新增/修改行为有测试或明确的 NOT_RUN 说明；
- [ ] 注释没有混入事故编号和修复历史。

---

## 15. 与现有规则的关系

- AGENTS.md 的硬规则、三步失效分析法继续有效；本文只补充 WPF/桌面生命周期相关的约束。
- 本文与 AGENTS.md 冲突时，以本文的 WPF 条款为准，并回头修正 AGENTS.md 的索引。
- 改动判定逻辑、并发/生命周期、配置读取、数据保留策略时，仍按 AGENTS.md 第 6 节跑对应切片。
- 本文是**目标状态**，不是现状描述；现状与迁移进度见 `docs/WPF规范落地记录.md`。
