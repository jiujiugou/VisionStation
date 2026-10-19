# AGENTS.md — VisionStation

> 本文件是**索引**，不是手册。任何 AI 会话开始时先读它，需要细节再按指路去读 `docs/`。
> 本文件与实际代码冲突时，以代码为准，并回头修正本文件（不要留着一份过期的索引）。
> 控制在 200 行内。写长了等于没写。

## 1. 这是什么

WPF 金属零件视觉检测工作站。取图 → 工具流水线判定（模板匹配 / 尺寸 / 缺陷）→ px↔mm 标定 → 配方驱动 → SQLite 存记录 → Serilog 落日志。

技术栈：.NET 8 · WPF · OpenCvSharp4 · EF Core + SQLite · xUnit · 海康 MVS SDK V4.x

## 2. 目录与禁区

| 路径 | 作用 | 能否修改 |
|---|---|---|
| `src/VisionStation.Core` | 算法、模型、配方、标定、流水线（**无 IO、无 WPF**） | 可改 |
| `src/VisionStation.Devices` | 相机（海康 / 文件 / 网页摄像头）、触发源 | 可改 |
| `src/VisionStation.Storage` | EF Core DbContext、检测仓储、图片存储 | 可改 |
| `src/VisionStation.Service` | 自检（HealthChecker）、管家（Housekeeping） | 可改 |
| `src/VisionStation.App` | WPF 组合根、DI 注册、ViewModel | 可改 |
| `src/VisionStation.Watchdog` | 独立进程守护 | 可改 |
| `tests/VisionStation.UnitTests` | 全部测试（当前 100 项） | **只准加，不准改断言** |
| `docs/` | 设计文档与规范，`三步失效分析法.md` 是方法准绳 | 可改 |
| `audit/` | 失效分析产物 + 门禁脚本 | 由流程生成，不手改数据 |
| `bin/` `obj/` `publish/` | 构建产物 | **禁止编辑** |
| `models/` | ONNX 权重 | **禁止编辑** |
| `Logs/` `InspectedImages/` `TestImages/` `*.db` | 运行期数据 | **禁止编辑**，也不要加入示例代码 |

依赖方向单向：`Core ← Devices ← Storage ← Service ← App`。Core 不得反向依赖任何工程。

## 3. 命令

```bat
dotnet build tests\VisionStation.UnitTests -v q --nologo    :: 编译检查（最快）
dotnet test  tests\VisionStation.UnitTests --nologo         :: 全量测试
dotnet test  tests\VisionStation.UnitTests --filter "FullyQualifiedName~Housekeeping" --nologo
verify.cmd                                                  :: 编译 + 测试 + 门禁（运维切片）
verify.cmd audit\artifacts-core\scope-core.json artifacts-core  :: 同上，Core 切片

:: 变异测试（只对纯数学/判定段跑，约 2 分钟；不要全仓跑）
dotnet dotnet-stryker -p src/VisionStation.Core/VisionStation.Core.csproj ^
  -tp tests/VisionStation.UnitTests/VisionStation.UnitTests.csproj ^
  -m "**/Calibration.cs" -m "**/Geometry/CoordinateMath.cs" -b 0
```

`verify.cmd` 退出码 0 才算做完。任何"已完成"的说法，必须先贴它的输出。

## 4. 硬规则（违反即作废重做）

1. **不许改测试让门禁变绿**。不许放宽断言、抬高阈值、加 `Skip`。测试红了就是代码错了。
2. **不许自己决定业务参数**：判定阈值、参数合法区间、保留天数、容量上限——一律问人。写"我按常见值取了 0.85"等于造假。
3. **每条结论必须可点开**：`文件:行号@符号`，文件要真存在、行号不许越界。引用旧代码前先确认它还在。
4. **修完追加不覆盖**：`V-xxx-retry`，原始 FAIL 记录保留。改了历史等于把"修了几次"抹掉。
5. **没做的写 NOT_RUN**。禁止"基本通过""应该没问题""已验证（无证据）"。
6. 一次性删改超过 5 个文件前，先说清楚改哪些、为什么。改完让人看 `git diff --stat`。

## 5. 已知坑（都是真实踩过的，别再犯）

1. **`InspectionDbContext` 不能注册成单例**。检测线程和管家定时器会并发撞车，记录静默丢失。`InspectionRepository` 每次操作自建、自释放 context。
2. **`Dispose` 必须等在途回调收尾**。`async void` + `Timer` 是标准雷区，现在 `HousekeepingService.Dispose` 会等最多 5 秒。
3. **配置阈值在构造函数里校验**。`ImageRetentionDays = 0` 等于删光当天全部图片，不许静默回落默认值。
4. **自检判据必须可证伪**。`File.Exists(f) || Directory.Exists(父目录)` 是恒真判据（父目录是程序自己建的），它让整个自检变成装饰品。
5. **图片保留期 ≥ 记录保留期**。否则第 8 天起，数据库记录指向的图全都不在了，追溯断链。
6. **海康 SDK 是进程级的**：`MV_CC_Finalize_NET` 会让所有相机实例失效，所以初始化/反初始化必须走引用计数（`SdkAcquire`/`ReleaseSdk`），只有最后一个实例能 Finalize。
7. **热切换失败必须回滚**。工业相机是独占设备：新的那个建不成时，绝不能替换指针、更不能释放旧实例（`SwapTo` 返回 bool，App 侧必须检查）。`CurrentMode` 要由实际相机推导，写死默认值会让 UI 的去重守卫失效。
8. **判定逻辑改一个数字就要配一个测试**。Core 里的阈值比较是"判定即产品"，错一位整条产线判错。
9. **空结果集不能判合格**。`Enumerable.All` 对空集合返回 true——空配方/全禁用/ROI 全没匹配上都会让`IsOk` 变 true，等于"没检=合格"。`InspectionReport.IsOk` 现在要求 `Results.Count > 0`；`DeviceManager.AllConnected` 同理要求 `Count > 0`。
10. **工具名是报告主键，重名会互相覆盖**。两个 `Presence` 工具同配方共存时，后执行的结果会顶掉前一个，被顶掉的 NG 凭空消失。`Pipeline` 构造期已改为重名即抛。
11. **NaN 能穿过 `<= 0` 守卫**。WPF 未测量时 `ActualWidth` 是 NaN，`NaN <= 0` 为 false，守卫形同虚设。尺寸校验一律用 `IsFinitePositive`（拒绝 NaN/∞/0/负）。
12. **在线状态必须可被反证**。设备层的 `IsAvailable` 不能只置真不置假：无帧超过 `StaleAfterMs` 就要转假，否则界面报就绪、采集返回 null、下游全部以为一切正常。
13. **后台线程的异常就地拦下**。`GrabLoop`/`Timer` 回调跑在无 catch 的线程上，未捕获异常 = 进程终止；且帧缓冲必须在 `finally` 里归还，漏一次 SDK 就再也不出帧。
14. **资源契约写进接口，不靠实现类自觉**。`ICamera` / `ITriggerSource` 都继承 `IDisposable`：新加一个相机忘写释放，编译器直接报错。不要给 `IDisposable.Dispose` 写 default interface 实现——那会让忘记实现的相机静默拿到空释放，正好把要防的泄漏放过去。替身/无资源实现显式写空 `Dispose` 是正确的，不是遗漏。
15. **Core 的公开签名泄漏了 `OpenCvSharp.Mat`**（`CameraFrame.Image`）。不影响运行，但意味着 Core 脱离 OpenCV 就用不了（命令行批处理、服务端复检都得带上 OpenCV）。要还这笔债得给 Core 加一层自有图像类型，成本不小，当前不动；换 UI 或做无界面批处理时一并处理。

## 6. 三步失效分析法（本仓库的工程纪律）

规范全文：`docs/三步失效分析法.md`（只写规则）。各切片跑完的结果、未清偿清单、方法修订史见
`docs/三步法落地记录.md`（只写现状）。两份文件不要互相掺内容。

切片各有一套产物：

| 切片 | 产物目录 | 范围 | 门禁 |
|---|---|---|---|
| 运维 | `audit/artifacts/` | U19/U20 + 5 交互 | `python audit\scripts\gate_check.py --scope audit\artifacts\scope-ops.json` |
| Core 主链 | `audit/artifacts-core/` | U01-U07 + E01/E02/E03/E04/E21 | `python audit\scripts\gate_check.py --art artifacts-core --scope audit\artifacts-core\scope-core.json` |
| 设备层 | `audit/artifacts-devices/` | U08-U15 + E05-E11 | `python audit\scripts\gate_check.py --art artifacts-devices --scope audit\artifacts-devices\scope-dev.json` |

三套共用 `audit/artifacts/index.json` 的结构索引；`--art` 只切换三份产物与证据的落点。
设备切片的 N/A 矩阵由 `audit/scripts/gen_devices_stage1.py` 生成，改网格后重跑它而不是手改 JSON。

每套产物固定三件：

- `FailureCatalog.json` — 故障清单（每条带 `文件:行号` 依据 + 危险等级 S1/S2/S3）
- `RequirementCatalog.json` — 约束清单（每条带 scope/trigger/criterion + 验证手段 + 可执行命令）
- `VerificationReport.json` — 验证报告（PASS / FAIL / UNVERIFIED / NOT_RUN 四态，各有证据文件）

什么时候必须跑：**新增或修改判定逻辑、并发/生命周期代码、配置读取、数据保留策略**时。
什么时候不必跑：纯 UI 布局、样式、日志文案、重命名。

收敛判据（防止把简单模块写出几十条问题）：
- 一行代码只许出一条缺陷，其余并入 `instances[]`
- 跑不出测试或复现的，一律降为 OBSERVATION，**不计入总数**
- 密度上限 60 条/KLOC，超了就是分析方式有问题，不是代码有问题

## 7. 做完的定义

1. `verify.cmd` 退出码 0（对应切片）；
2. 新增的缺陷条目有 `文件:行号` 依据 + 一个能跑的测试（不能跑的写 NOT_RUN 并说明缺什么）；
3. 三份产物与实际代码同步，`docs/三步失效分析法.md` 的规则没被绕过；
4. `git diff` 里没有测试文件的断言被改动；
5. 改动 `Calibration`/`CoordinateMath`/判定阈值后，变异测试得分不低于 90%，存活变异逐条裁决（补断言或标等价）。
