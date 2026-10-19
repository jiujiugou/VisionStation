#!/usr/bin/env python3
"""阶段 1 生成器：App 切片的 189 格判定。

与 gen_storage_stage1.py 同一套自检，但归并规则按规程 §2 第 2 条放宽：
  同一「目标×类别」允许多条 FOUND（不同 path:line 是不同根因，
  如 U21/无效 里自检弹框与配置热更新就是两件事），
  只有同一 path:line 被登记两次才拦（那才是重复计数，门禁也拦）。

缺格 / N/A 表多余键直接退出：漏格在门禁里只显示"缺 N 格"，看不出是哪一格没想过。
"""
import json
import os

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "audit", "artifacts-app", "FailureCatalog.json")
SCOPE = os.path.join(ROOT, "audit", "artifacts-app", "scope-app.json")

CATS = ["无效", "缺失", "延迟", "重复", "失败", "阻塞", "崩溃", "资源耗尽", "恢复失败"]

ENTRIES = [
    # ══════════════════════════ MAIN ══════════════════════════
    {
        "id": "F-A01",
        "target": "U21",
        "category": "无效",
        "status": "FOUND",
        "tier": "MAIN",
        "danger_level": "S1",
        "reachability": "REACHABLE",
        "code_ref": "src/VisionStation.App/App.xaml.cs:109@MessageBox.Show",
        "instances": [{"target": "E26", "category": "无效"}],
        "trigger": (
            "启动自检 5 项里任意一项 FAIL（存储目录不可写、相机离线、模板缺失、配方含未知工具）时，"
            "循环体内只做两件事：写一行日志、弹一个模态框。点掉后不改变任何系统状态，"
            "继续 Show 主窗口并开始接受触发。"
        ),
        "impact": (
            "两个后果，都发生在生产线上：① 带缺陷运行 —— 自检是启动期唯一的把关，"
            "它的结论没有接线到任何开关，操作员点掉对话框即进入「少一个工具 / 存不了图 / 没相机」"
            "的生产状态，判定会静默变松；② 无人值守时启动被模态框卡死 —— 进程活着但主窗口永不出现，"
            "看门狗的 WaitForExit 等不到退出，不会重启，产线停摆而监控显示「进程在跑」。"
            "同一份文件的崩溃处理器注释里明确写着「绝不能弹模态框，模态框会卡住 Dispatcher，"
            "看门狗彻底失效」，自检路径正是这么做的。"
        ),
        "observable": (
            "把 appsettings.json 的 Storage:ImageFolder 指向一个只读目录后双击启动：屏幕上弹出"
            "「存储路径: 目录不可写」对话框；不点它，主窗口永远不出现，任务管理器里 "
            "VisionStation.App.exe 与 VisionStation.Watchdog.exe 都活着；点掉它，主窗口出现、检测照跑，"
            "日志里每帧一条「存图失败（连续 N 次）」，状态栏 OK 计数继续增长。"
        ),
        "disposition": "OPEN",
    },
    {
        "id": "F-A02",
        "target": "U22",
        "category": "资源耗尽",
        "status": "FOUND",
        "tier": "MAIN",
        "danger_level": "S2",
        "reachability": "REACHABLE",
        "code_ref": "src/VisionStation.App/ViewModels/MainViewModel.cs:154@var frame = _camera.Capture();",
        "instances": [{"target": "E19", "category": "资源耗尽"}],
        "trigger": (
            "每帧 Capture 返回新的 CameraFrame，其 Image 是新建的 Mat（HikCamera.RawToMat、"
            "FileCamera.ImRead 都是每帧新建）。ProcessFrame 只释放自己 Clone 出来的 anno，"
            "frame.Image 从取到到方法结束没有任何 Dispose —— 全仓库唯一显式释放它的是 "
            "tests/VisionStation.Test/Program.cs:105。"
        ),
        "impact": (
            "每帧一块非托管内存只靠 OpenCvSharp 终结器回收。检测主循环是 UI 线程上的短任务、"
            "托管分配很小，GC 不会因为像素占用非托管内存而提前触发，工作集随帧数单调上涨："
            "1280×1024 每帧约 3.9MB，1 秒一帧就是一小时十几 GB 的分配量，"
            "最终表现为 OpenCV 分配失败、整机换页卡顿甚至 OutOfMemory。"
            "磁盘上图片齐全、日志里没有任何「内存」字样，从外部完全看不出是这里漏的。"
        ),
        "observable": (
            "用文件相机循环跑 30 分钟（1 秒一帧）：任务管理器里专用工作集从约 300MB 涨到 800MB 以上"
            "且不回落，Stopwatch 单帧耗时逐渐变长，重启进程后归零。"
            "探针实测（300×300 图 50 帧）：私有字节 85MB → 93MB，强制 GC+终结器后回落到 85MB —— "
            "释放完全依赖终结器。"
        ),
        "disposition": "OPEN",
    },
    {
        "id": "F-A03",
        "target": "U22",
        "category": "缺失",
        "status": "FOUND",
        "tier": "MAIN",
        "danger_level": "S2",
        "reachability": "REACHABLE",
        "code_ref": "src/VisionStation.App/ViewModels/MainViewModel.cs:206@await _repo.SaveAsync(report);",
        "instances": [{"target": "E21", "category": "缺失"}],
        "trigger": (
            "落库失败（磁盘满、库被独占、schema 与代码不一致）时 SaveAsync 抛异常，"
            "被外层 catch 统一记成「检测异常」。而状态栏计数、InspectionResultMessage、"
            "CameraStatusMessage 全部排在它之后。"
        ),
        "impact": (
            "检测已经跑完、图已经存好、判定已经在手里，但界面不显示这一次的结果："
            "结果面板停在上一件、OK/NG 计数不增、日志只有一行「检测异常」。"
            "操作员看到的是「这一件没检测」，实际是「检测了但没送到界面」；"
            "NG 件被静默跳过，产线照常放行。失败点在持久化，陪葬的却是显示。"
        ),
        "disposition": "OPEN",
    },
    {
        "id": "F-A04",
        "target": "U23",
        "category": "崩溃",
        "status": "FOUND",
        "tier": "MAIN",
        "danger_level": "S1",
        "reachability": "REACHABLE",
        "code_ref": "src/VisionStation.App/ViewModels/RoiEditorViewModel.cs:298@File.WriteAllText(_paths.RecipePath, json);",
        "instances": [
            {"target": "U23", "category": "恢复失败"},
            {"target": "E24", "category": "崩溃"},
        ],
        "trigger": (
            "保存 ROI 时目标路径不可写（软件装在 Program Files、共享目录只读、盘符不存在、磁盘满）："
            "Directory.CreateDirectory 或 WriteAllText 抛异常，方法体内没有任何 try/catch，"
            "RelayCommand 的同步 Execute 也不吞异常。同一行还是「直接覆盖写」——写一半断电或崩溃，"
            "磁盘上就留下半截 JSON。"
        ),
        "impact": (
            "① 崩溃：异常冒到 WPF 输入循环，App 的 DispatcherUnhandledException 里 Handled=false → "
            "进程退出 → 看门狗重启；当前编辑的 ROI 全部丢失，界面没有任何提示，"
            "操作员的感受是「点保存程序就没了」。"
            "② 静默错配方：半截 JSON 下次启动解析失败 → LoadRecipe 回退内嵌默认配方（只写一行 Warning）→ "
            "标定值与 ROI 全是默认值，同一件昨天 NG 今天 OK，界面没有任何「当前跑的是默认配方」的提示。"
        ),
        "observable": (
            "① 把配方目录设为只读后点「保存」：窗口直接消失、约 2 秒后又被拉起，"
            "crash.log 里一条 DirectoryNotFoundException 或 UnauthorizedAccessException，ROI 改动没了。"
            "② 保存过程中强制结束进程，重启后日志出现「配方 ... 加载失败（缺失/损坏/为空），回退到内嵌默认配方」，"
            "ROI 框位置与昨天不同，直径判定改用默认 15px/mm 而不是现场标定值，合格率突然变好。"
        ),
        "disposition": "OPEN",
    },
    {
        "id": "F-A05",
        "target": "U24",
        "category": "无效",
        "status": "FOUND",
        "tier": "MAIN",
        "danger_level": "S2",
        "reachability": "REACHABLE",
        "code_ref": "src/VisionStation.App/ViewModels/ControlPanelViewModel.cs:134@if (_camera is FileCamera fc)",
        "trigger": (
            "DI 把 ICamera 注册成 SwappableCamera（ServiceCollectionExtensions.cs:64），"
            "ControlPanelViewModel 注入的是 ICamera。`_camera is FileCamera` 在任何配置下都是 false —— "
            "包括真正用文件相机的时候（外面多包了一层 SwappableCamera）。"
        ),
        "impact": (
            "「重连」按钮只会读一次 IsAvailable 并广播，永远不会去重新扫描图片目录、也不会重开相机。"
            "现场换了一批待检图片、或相机掉线后重插，点重连只会显示「相机无图像」，其实什么都没做。"
            "按钮存在、功能是空的，而且失败方向是「看起来在报错」，比静默更迷惑。"
        ),
        "observable": (
            "清空 File 相机的图片目录 → 界面提示「相机无图像」；把新图片放进目录 → 点「重连」，"
            "日志仍打「相机无图像」；直到在相机页切换一次模式（走 ApplySwitch）才恢复。"
        ),
        "disposition": "OPEN",
    },
    {
        "id": "F-A06",
        "target": "U29",
        "category": "无效",
        "status": "FOUND",
        "tier": "MAIN",
        "danger_level": "S2",
        "reachability": "CONSTRUCTIBLE",
        "code_ref": "src/VisionStation.App/Services/CameraFactory.cs:41@_ => new FileCamera(",
        "instances": [{"target": "U25", "category": "无效"}],
        "trigger": (
            "CreateByMode 的 switch 没有 default 报错分支：模式名大小写不符（HikVision / HIK）"
            "或拼错（Hikvison）都落到 FileCamera。SwappableCamera 用独立的 initialMode 参数记录模式名，"
            "CameraPage 又把模式名原样映射成显示文本。"
        ),
        "impact": (
            "现场把 appsettings 的 Camera:Mode 写错一个字母：真实设备是文件相机（反复读同一批测试图），"
            "相机页却显示「HikVision」，状态栏显示「FileCamera」。检测结果全部来自测试图，"
            "产品件根本没被拍 —— 没有日志、没有自检报错（自检只看设备是否可用）。"
            "这是「检查者与被检查对象不是同一个东西」的典型形态。"
        ),
        "observable": (
            "把 Camera:Mode 改成 \"HikVision\" 后启动：相机页显示「HikVision」，状态栏图像源是「FileCamera」，"
            "同一张图片被反复判定、每帧结果完全相同；改回 \"Hikvision\" 才切到真相机。"
            "探针实测：Mode=HikVision → 实例 FileCamera，界面显示 \"HikVision\"。"
        ),
        "disposition": "OPEN",
    },
    {
        "id": "F-A07",
        "target": "U28",
        "category": "缺失",
        "status": "FOUND",
        "tier": "MAIN",
        "danger_level": "S2",
        "reachability": "REACHABLE",
        "code_ref": "src/VisionStation.App/Services/ToolFactory.cs:99@System.Diagnostics.Debug.WriteLine($\"[ToolFactory] 未知工具类型: {tc.Type}\");",
        "instances": [{"target": "E20", "category": "缺失"}],
        "trigger": (
            "配方里出现不在 switch 里的 Type（打错字、旧版本配方、新工具没编译进来）："
            "CreateOne 返回 null，CreateAll 只是少 add 一个；唯一痕迹是 Debug.WriteLine —— "
            "Release 版根本不会进任何日志文件。"
        ),
        "impact": (
            "工具静默缺席，总判定只按剩下的工具算：少了缺陷检测那一项，带缺陷的件会被判 OK，"
            "而日志里连一行 Error 都没有。自检能报「未知工具类型」，但这个结论只走到一个模态框（F-A01），"
            "点掉即消失，不留任何持久痕迹。"
        ),
        "observable": (
            "把配方里 Defect 的 Type 改成 \"Defects\" 后启动：结果面板只剩三行（划痕/缺口固定显示 OK 0 处），"
            "日志没有任何「未知工具」记录，带划痕的图片被判 OK。"
            "探针实测：4 个启用工具 → 实际生成 3 个，无异常、无日志。"
        ),
        "disposition": "OPEN",
    },
    {
        "id": "F-A08",
        "target": "U31",
        "category": "缺失",
        "status": "FOUND",
        "tier": "MAIN",
        "danger_level": "S2",
        "reachability": "REACHABLE",
        "code_ref": "src/VisionStation.App/MainWindow.xaml.cs:52@RoiEditor.Visibility = Visibility.Collapsed;",
        "instances": [{"target": "E25", "category": "缺失"}],
        "trigger": (
            "F2 进入 ROI 编辑态时把 ImagePanel 设成 Collapsed；切页处理器只收起 RoiEditor、"
            "把 _isEditMode 置 false，从不恢复 ImagePanel。而 F2 的快捷键只在 InspectionPage 可见时生效。"
        ),
        "impact": (
            "F2 → 点任意导航项之后，检测页的图像区永久空白（本进程内有效），"
            "只能靠再按两次 F2 把 ImagePanel 显回来；如果已经切到别的页，F2 又不起作用，只能重启程序。"
            "现场表现是「检测页没图像了，但检测还在跑」，操作员会怀疑相机。"
        ),
        "observable": (
            "启动后按 F2 进入 ROI 编辑，点侧边栏「参数」，再点「检测」：右半边图像区一片空白，"
            "结果面板与日志正常刷新；此时按 F2 无反应（InspectionPage 已不可见），必须重启。"
        ),
        "disposition": "OPEN",
    },
    {
        "id": "F-A09",
        "target": "U27",
        "category": "无效",
        "status": "FOUND",
        "tier": "MAIN",
        "danger_level": "S2",
        "reachability": "REACHABLE",
        "code_ref": "src/VisionStation.App/Services/AppPaths.cs:80@Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, rel))",
        "instances": [{"target": "E21", "category": "无效"}],
        "trigger": (
            "所有输出路径（日志、图片、数据库）都以 Environment.CurrentDirectory 为基准拼接，"
            "配置里没有可锚定的基准目录；只有输入资源在找不到时回退 AppContext.BaseDirectory。"
        ),
        "impact": (
            "计划任务（默认 cwd=System32）、服务方式启动、快捷方式起始位置被改过时，"
            "数据与库会落到 System32 或程序目录这类不可写、无人知晓的位置：日志写不进去、"
            "SQLite 建库失败、图片全部存不了，而程序本身照常启动（自检只弹框，见 F-A01）。"
            "同一台机器用两种方式启动会得到两套数据库，历史记录「凭空消失」。"
        ),
        "observable": (
            "从开始菜单快捷方式（起始位置留空）启动一次，再从仓库根目录启动一次：前者的 visionstation.db "
            "出现在 C:\\Windows\\System32 下，后者在仓库根；前一小时检的 200 条记录在后一次里一条都看不到。"
            "探针实测：改 cwd 后 DbPath 跟着变，两个 cwd 下路径不相同。"
        ),
        "disposition": "OPEN",
    },
    {
        "id": "F-A10",
        "target": "U22",
        "category": "无效",
        "status": "FOUND",
        "tier": "MAIN",
        "danger_level": "S3",
        "reachability": "CONSTRUCTIBLE",
        "code_ref": "src/VisionStation.App/ViewModels/MainViewModel.cs:217@_statusBar.Fps = $\"{1000.0 / _emaCycleMs:0.0}\";",
        "trigger": (
            "CycleTime 取 Stopwatch.ElapsedMilliseconds（整数毫秒），小图/快帧时取整为 0；"
            "_emaCycleMs 的初始化分支直接取 cycleMs，于是除数为 0。"
        ),
        "impact": (
            "1000.0/0 在 double 下是 Infinity，格式化后状态栏 FPS 显示「∞」。不是崩溃，"
            "但读数失真会误导节拍判断，也掩盖了「这一帧确实小于 1ms」这个信息。"
        ),
        "observable": "用 300×300 的测试图连跑几帧：状态栏 FPS 显示「∞」，CycleTime 显示「0 ms」。",
        "disposition": "OPEN",
    },
    {
        "id": "F-A11",
        "target": "U32",
        "category": "无效",
        "status": "FOUND",
        "tier": "MAIN",
        "danger_level": "S3",
        "reachability": "REACHABLE",
        "code_ref": "src/VisionStation.App/Views/ImagePanelView.xaml.cs:28@ImageDisplay.Source = _vm.CurrentImage;",
        "trigger": "新图像到来时只换 Source，_zoom 与 PanTransform 保持上一张的状态；复位逻辑只挂在 F 键上。",
        "impact": (
            "上一件放大到 20 倍看局部后，下一件仍以 20 倍和平移偏移显示，看到的可能是一块空白或非检测区域；"
            "连续生产时每件都要手动按一次 F，容易把「视角没复位」误读成「图像没更新」。"
        ),
        "observable": "放大到 5 倍并把图平移到右下角，等下一帧到达：图像仍是 5 倍且停在角落，坐标读数还是旧位置。",
        "disposition": "OPEN",
    },
    {
        "id": "F-A12",
        "target": "U26",
        "category": "阻塞",
        "status": "FOUND",
        "tier": "MAIN",
        "danger_level": "S3",
        "reachability": "CONSTRUCTIBLE",
        "code_ref": "src/VisionStation.App/ViewModels/LogPanelViewModel.cs:53@System.Windows.Application.Current?.Dispatcher.Invoke(() =>",
        "instances": [{"target": "E22", "category": "阻塞"}],
        "trigger": (
            "每条日志都走同步 Dispatcher.Invoke：发日志的若是后台线程（管家、采图线程、看门狗回调），"
            "该线程被阻塞到 UI 线程空闲为止；窗口关闭期间 Dispatcher 已关闭，Invoke 抛 "
            "TaskCanceledException，这里没有 try/catch。"
        ),
        "impact": (
            "日志量一大（每帧 4~6 条）就把后台线程拖在 UI 渲染后面排队，管家心跳与采图时序被界面速度绑架；"
            "退出瞬间产生的日志会抛未观察异常。日志是旁路，不该有阻塞主流程的能力。"
        ),
        "observable": (
            "自动模式跑到 5 帧/秒、日志面板打开时，管家心跳与采图间隔开始抖动（日志时间戳间隔不均）；"
            "关闭窗口的瞬间输出窗口出现 TaskCanceledException。"
        ),
        "disposition": "OPEN",
    },
    {
        "id": "F-A13",
        "target": "U30",
        "category": "无效",
        "status": "FOUND",
        "tier": "MAIN",
        "danger_level": "S3",
        "reachability": "REACHABLE",
        "code_ref": "src/VisionStation.App/Services/ServiceCollectionExtensions.cs:66@AddSingleton<ITriggerSource>",
        "trigger": (
            "appsettings.json 里有 \"Trigger:Mode\": \"Manual\"，但注册处写死 TimerTrigger，"
            "全仓库没有任何代码读 Trigger:Mode。"
        ),
        "impact": (
            "配置项是个死键：现场按注释把 Mode 改成 PLC/Manual，期待「改配置即切触发源」，"
            "实际仍走定时器。改配置 → 重启 → 行为不变，排障时会误以为是定时器实现的问题。"
        ),
        "observable": "把 Trigger:Mode 改成 \"PLC\" 并重启：界面照旧每 2 秒自动采一帧，日志里没有任何「未知触发模式」提示。",
        "disposition": "OPEN",
    },
    {
        "id": "F-A14",
        "target": "U21",
        "category": "恢复失败",
        "status": "FOUND",
        "tier": "MAIN",
        "danger_level": "S3",
        "reachability": "CONSTRUCTIBLE",
        "code_ref": "src/VisionStation.App/App.xaml.cs:147@if (recipe != null && recipe.Tools.Count > 0) return recipe;",
        "trigger": (
            "配方文件解析成功但 Tools 为空（调试时把工具全禁用/删空）→ 条件不成立 → 落到兜底分支，"
            "返回内嵌默认配方（4 个工具全开），只记一行 Warning。"
        ),
        "impact": (
            "「操作员主动禁用全部工具」与「配方文件损坏」被同一个兜底处理：程序启动后满血跑 4 个工具，"
            "与现场预期的「什么都不检」相反；配置与行为不一致，且没有任何界面提示当前用的是内嵌配方。"
        ),
        "observable": (
            "把 default.json 的 tools 改成 [] 后启动：日志出现「配方 ... 加载失败（缺失/损坏/为空），"
            "回退到内嵌默认配方」，而结果面板出现四行检测项、给出 OK/NG 判定 —— 文件本身是合法的空配方。"
        ),
        "disposition": "OPEN",
    },
    {
        "id": "F-A15",
        "target": "U28",
        "category": "失败",
        "status": "FOUND",
        "tier": "MAIN",
        "danger_level": "S3",
        "reachability": "CONSTRUCTIBLE",
        "code_ref": "src/VisionStation.App/Services/ToolFactory.cs:118@if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))",
        "trigger": (
            "YOLO 模型解析只看 File.Exists，不试加载；OnnxYoloTool 的 InferenceSession 是懒建的"
            "（首次 Execute 才建），模型损坏或 opset 不匹配时到第一帧才抛，被 Pipeline catch 成该工具 NG。"
        ),
        "impact": (
            "模型文件坏掉（下载不完整、换了 onnx 版本）→ 启动正常、自检通过 → 第一帧起该工具恒 NG → "
            "全线每件都判 NG，停机排障时日志只有一行「OnnxYoloTool 执行异常」。"
            "与此对照，「模型缺失」这条路径反倒是有意跳过的 —— 两种坏法给出相反的降级行为，没人解释得清。"
        ),
        "observable": (
            "用一个改了扩展名的文本文件替代 models/neu-det.onnx：启动无异常，自检全绿，第一帧起每件都判 NG，"
            "日志每帧一条工具异常；把该文件删掉反而恢复正常（工具被跳过）。"
        ),
        "disposition": "OPEN",
    },
    {
        "id": "F-A16",
        "target": "E22",
        "category": "缺失",
        "status": "FOUND",
        "tier": "MAIN",
        "danger_level": "S3",
        "reachability": "REACHABLE",
        "code_ref": "src/VisionStation.App/ViewModels/ResultPanelViewModel.cs:50@vm.Rows.Clear();",
        "trigger": (
            "报告里工具数不等于 3（配方启用 Yolo，或将来加 OCR）：面板固定只把 Presence/Dimension/Defect "
            "映射成四行，其它工具的结果既不显示也不提示。"
        ),
        "impact": (
            "界面出现「总判定 NG，四行全 OK」：Yolo 判出的缺陷不入表，操作员无法解释 NG 从哪来，"
            "只能怀疑系统误判。这条记录在库里是完整的（ToolResultsJson 有），但界面查不到 —— "
            "现场复盘只能靠翻 JSON。"
        ),
        "observable": (
            "配方启用 Yolo 并对一张有缺陷的图跑一帧：总判定条红色 NG，表格四行全 OK，日志写「判定:NG」，"
            "界面与日志互相矛盾。探针实测：4 个工具其中 1 个 NG → 表格 4 行全 OK、总判定 NG。"
        ),
        "disposition": "OPEN",
    },

    # ══════════════════════ OBSERVATION ══════════════════════
    {
        "id": "O-A01",
        "target": "U22",
        "category": "延迟",
        "status": "FOUND",
        "tier": "OBSERVATION",
        "danger_level": "S3",
        "reachability": "REACHABLE",
        "code_ref": "src/VisionStation.App/ViewModels/MainViewModel.cs:146@if (_isProcessing) return;",
        "instances": [{"target": "E18", "category": "延迟"}],
        "trigger": "上一帧还在跑（含 await 落库）时新触发到达 → 直接 return，不计数、不记日志。",
        "impact": (
            "触发比检测快时（定时器 2s 但某帧因存图/DB 抖动变慢），被丢的帧数没有任何地方可见；"
            "状态栏 FPS 显示的是处理速率，操作员会以为这就是产线节拍。当前节拍下丢弃窗口极窄，不构成功能失效。"
        ),
        "next_action": "DEFERRED",
        "deferred_reason": (
            "当前约 1 帧/秒、单帧 100~300ms，丢弃窗口只在 IO 抖动时打开；加计数要动状态栏布局与消息，"
            "属可观测性增强而非故障修复，本版不做。"
        ),
        "review_condition": "一旦现场出现「实际过件数与 OK+NG 对不上」，必须加丢帧计数并显示在状态栏。",
        "disposition": "OPEN",
    },
    {
        "id": "O-A02",
        "target": "U21",
        "category": "阻塞",
        "status": "FOUND",
        "tier": "OBSERVATION",
        "danger_level": "S3",
        "reachability": "CONSTRUCTIBLE",
        "code_ref": "src/VisionStation.App/App.xaml.cs:202@try { (Services as IDisposable)?.Dispose(); } catch { }",
        "trigger": (
            "致命异常处理器里同步释放整个 DI 容器：容器会依次 Dispose HikCamera（MV_CC_CloseDevice 同步调 SDK）、"
            "SQLite 连接、Serilog。"
        ),
        "impact": (
            "在崩溃线程上做同步 IO/SDK 调用，若 SDK 在异常状态下卡住，进程迟迟不退出，"
            "看门狗 WaitForExit 等不到，重启守护失效；而紧邻的注释明确写着「绝不做任何可能阻塞的操作」。"
        ),
        "next_action": "DEFERRED",
        "deferred_reason": (
            "只有 SDK 自身卡死时才成立，未观测到；改成异步释放或带超时需要一个专门的崩溃收尾线程，"
            "风险高于当前收益。"
        ),
        "review_condition": "现场出现「崩溃后进程一直不退出、看门狗没拉起」时，必须把 Fatal 内的释放改成带超时的后台任务。",
        "disposition": "OPEN",
    },
    {
        "id": "O-A03",
        "target": "U21",
        "category": "无效",
        "status": "FOUND",
        "tier": "OBSERVATION",
        "danger_level": "S3",
        "reachability": "CONSTRUCTIBLE",
        "code_ref": "src/VisionStation.App/App.xaml.cs:67@.AddJsonFile(\"appsettings.json\", optional: false, reloadOnChange: true)",
        "trigger": (
            "打开了 reloadOnChange，但 IConfigurationRoot 没有被保存，也没有 IOptionsMonitor/ChangeToken 订阅；"
            "AppPaths、配方、管家阈值都在启动时就读成构造参数。"
        ),
        "impact": (
            "改 appsettings.json 会触发重载，但没有任何消费方拿到新值：「改配置不用重启」的预期落空，"
            "而且没有任何提示。属承诺与实现不一致，不造成错误判定。"
        ),
        "next_action": "DEFERRED",
        "deferred_reason": (
            "真正做热更新要把 AppPaths/阈值/相机模式全改成可重读的配置提供者，属架构变更；"
            "当前操作手册是「改完重启」。"
        ),
        "review_condition": "若现场要求不停机改阈值（内存上限、保留天数），必须先做配置提供者化，不能只留 reloadOnChange 这个空承诺。",
        "disposition": "OPEN",
    },
    {
        "id": "O-A04",
        "target": "U25",
        "category": "恢复失败",
        "status": "FOUND",
        "tier": "OBSERVATION",
        "danger_level": "S3",
        "reachability": "CONSTRUCTIBLE",
        "code_ref": "src/VisionStation.App/ViewModels/CameraPageViewModel.cs:64@if (!_swappable.SwapTo(SelectedMode))",
        "trigger": "切换相机成功只改内存里的 _active 与 _currentMode，不回写 appsettings.json。",
        "impact": (
            "重启后回到配置文件里的模式：操作员在界面上切到 USB 相机，第二天开机又是海康，"
            "若海康没接就一路「无图像」，现场会以为切换功能坏了。"
        ),
        "next_action": "DEFERRED",
        "deferred_reason": (
            "写配置需要一条「配置落盘」通道并处理写失败（与 F-A04 同一类风险）；"
            "当前以配置文件为唯一真相是明确的设计选择。"
        ),
        "review_condition": "若出现「切换后重启失效」的投诉，或引入多工位/多配方，必须把相机模式纳入可持久化配置。",
        "disposition": "OPEN",
    },
    {
        "id": "O-A05",
        "target": "U23",
        "category": "缺失",
        "status": "FOUND",
        "tier": "OBSERVATION",
        "danger_level": "S3",
        "reachability": "REACHABLE",
        "code_ref": "src/VisionStation.App/ViewModels/RoiEditorViewModel.cs:282@SelectedRoi.Name = SelectedRoi.Name.EndsWith(\"*\")",
        "trigger": "「重命名」命令没有输入框，只在名字尾部加/去一个 \"*\"。",
        "impact": (
            "操作员以为改了名，实际 ROI 名变成 \"ROI-1*\"，与配方里 Tool.Roi 写的 \"ROI-1\" 对不上，"
            "Pipeline 走索引兜底（有 Warning 日志但界面无感知），框可能画到另一块区域。"
        ),
        "next_action": "DEFERRED",
        "deferred_reason": (
            "真正的重命名需要输入对话框并与 Tool.Roi 联动改名，属功能补齐；"
            "当前 \"*\" 是明显的占位实现，未在产线使用。"
        ),
        "review_condition": "一旦配方里出现带 * 的 ROI 名，必须立刻补输入框或直接移除该按钮。",
        "disposition": "OPEN",
    },
    {
        "id": "O-A06",
        "target": "U23",
        "category": "无效",
        "status": "FOUND",
        "tier": "OBSERVATION",
        "danger_level": "S3",
        "reachability": "REACHABLE",
        "code_ref": "src/VisionStation.App/ViewModels/RoiEditorViewModel.cs:140@_drawStart = imgPt;",
        "instances": [{"target": "E23", "category": "无效"}],
        "trigger": (
            "鼠标坐标来自整个 Overlay 容器，换算后图像坐标可以为负（点在黑边上）或超出图像尺寸；"
            "画框/拖拽/缩放都不做 clamp，保存时直接 (int) 截断写进配方。"
        ),
        "impact": (
            "配方里出现 x<0 或 x+w>图像宽 的 ROI，下次检测裁切越界 → 工具抛异常 → 该工具恒 NG"
            "（Pipeline 把异常转成 NG），表现为「某块区域永远不合格」，而配方看上去只是坐标有点怪。"
        ),
        "next_action": "DEFERRED",
        "deferred_reason": (
            "当前操作员只在有效区域内画框；加 clamp 要同时定义拖拽/缩放三条路径的边界语义（贴边还是拒绝），"
            "先登记现象，本轮不引入行为变更。"
        ),
        "review_condition": "出现「某个 ROI 一到检测就 NG」或配方里出现负坐标时，必须在 MouseUp 与 SaveToFile 两处夹紧到图像边界。",
        "disposition": "OPEN",
    },
]

NA = {
    # ── U21 App ──
    ("U21", "缺失"): "启动序列该做的都做了（配置→路径→日志→DI→配方→自检→建库→显窗）；「自检失败后该不该继续」这一环的缺口登在 F-A01，不重复。",
    ("U21", "延迟"): "启动期 4 次同步 IO（配置、配方、建库、自检探针）合计 <1s；相机打开耗时的根因在 Devices 切片，属一次性成本。",
    ("U21", "重复"): "进程只启动一次；重复启动会被海康设备独占与同名看门狗挡下，不会出现双份检测。",
    ("U21", "失败"): "启动失败统一进 catch → LogCrash + Shutdown(1)，异常不外逃；确定性失败由看门狗次数上限收敛，不会无限抖动。",
    ("U21", "崩溃"): "三个入口（Dispatcher / TaskScheduler / AppDomain）都注册了处理器，且都只留痕不弹窗，崩溃不会静默消失。",
    ("U21", "资源耗尽"): "容器与 3 个事件订阅在 OnExit/Fatal 释放，看门狗子进程随父进程退出；没有随运行时间累积的结构。",

    # ── U22 MainViewModel ──
    ("U22", "重复"): "_isProcessing 挡住重入；一帧只发一条 InspectionResultMessage 与 CameraStatusMessage，无重复消费。",
    ("U22", "失败"): "帧内异常统一 catch 并记「检测异常」；存图失败另有连续 10 次的升级告警（Storage 切片已修那条链路）。",
    ("U22", "阻塞"): "唯一等待点是同步存图与 SaveAsync 的数据库往返；锁竞争形态已由 Storage F-S04 处理，本层不重复。",
    ("U22", "崩溃"): "帧处理全程在 try 内；空帧显式判空返回；MatToBitmap 自己 Clone/CvtColor 后释放，不用调用方的 Mat 生命周期。",
    ("U22", "恢复失败"): "帧级无跨帧状态（除累计计数），一帧失败不影响下一帧采图与判定。",

    # ── U23 RoiEditorViewModel ──
    ("U23", "延迟"): "拖拽时每次 MouseMove 重算各 ROI 的显示矩形，O(ROI 数)；框数在个位数，单次几十微秒。",
    ("U23", "重复"): "保存一次只发一条 RoisUpdatedMessage，MainViewModel 也只全量重载一次。",
    ("U23", "失败"): "保存失败的表现登在 F-A04（无 catch、非原子）；LoadFromFile 只读内存里的 RecipeConfig，没有读盘失败路径。",
    ("U23", "阻塞"): "同步写一个几十 KB 的 JSON，微秒级；无锁、无等待队列。",
    ("U23", "资源耗尽"): "不持句柄与图像副本；BitmapSource 由 MainWindow 从 ImagePanel 传入共享。",

    # ── U24 ControlPanelViewModel ──
    ("U24", "缺失"): "急停只停触发源、不输出硬件制动——本项目零通讯层（无 PLC/串口），属范围外而非本层遗漏；语义已写成「停止触发」。",
    ("U24", "延迟"): "命令都是同步转发（Fire/Start/Stop/MessageBox），没有排队、缓冲与定时。",
    ("U24", "重复"): "重复点自动模式是幂等的（取反后连点两次回到原状态）；Start/Stop 由 ITriggerSource 自己保证幂等。",
    ("U24", "失败"): "硬件方法抛出的异常不在此捕获——该形态合并登在 F-A05 的触发链（控件直接调硬件、无 try）。",
    ("U24", "阻塞"): "Fire/Stop 是事件与定时器开关，不等待；急停的 MessageBox 是人工确认，不是自动流程阻塞。",
    ("U24", "崩溃"): "ToggleAutoMode 里 Start() 抛异常会外逃至输入循环；与 F-A05 同属「控件直调硬件、无 try」，合并登记不重复。",
    ("U24", "资源耗尽"): "只持注入的单例引用，不新建相机/定时器/线程。",
    ("U24", "恢复失败"): "无跨命令状态需要恢复；相机掉线后的恢复入口是重连（F-A05）与相机页（F-A06）。",

    # ── U25 CameraPageViewModel ──
    ("U25", "缺失"): "相机列表来自 SwappableCamera.AvailableModes 静态数组，新增相机类型要改 Devices 层；属装配而非本层缺失。",
    ("U25", "延迟"): "SwapTo 同步打开设备（SDK 百毫秒级），是用户点击后的显式操作，不是自动路径。",
    ("U25", "重复"): "SelectedMode == CurrentMode 时直接 return，重复点同一模式不会重复开设备。",
    ("U25", "失败"): "SwapTo 返回 false 时回滚 SelectedMode 并发 Error 日志，失败路径有明确反馈。",
    ("U25", "阻塞"): "切换在 UI 线程同步执行，最长等一次设备打开；无队列、无锁等待。",
    ("U25", "崩溃"): "ApplySwitch 有 try/catch 兜底；ReconnectCamera 调 camDev.Connect() 无 catch，形态与 F-A05 同类，未单独成立条目。",
    ("U25", "资源耗尽"): "不持设备句柄；旧实例释放由 SwappableCamera.SwapTo 负责（Devices 切片已审）。",

    # ── U26 LogPanelViewModel ──
    ("U26", "无效"): "过滤靠枚举序（Info<Success<Warning<Error）比较，是有意设计；切换环与 FilterLabel 一一对应。",
    ("U26", "缺失"): "面板只显示消息总线上的日志，Serilog 文件是另一条通道；两者不要求一致，属设计。",
    ("U26", "延迟"): "插入 O(1) + 一次 ICollectionView 刷新；200 条上限使刷新成本固定。",
    ("U26", "重复"): "同一条消息只插一条；超过 200 条从尾部丢弃，不会重复计数。",
    ("U26", "失败"): "AddEntry 无返回值、无 IO；唯一失败形态是 Dispatcher 已关闭（登在 F-A12）。",
    ("U26", "崩溃"): "集合操作全在 UI 线程（Invoke 内）执行，不存在跨线程改 ObservableCollection。",
    ("U26", "资源耗尽"): "MaxEntries=200 硬上限，集合不随时间增长。",
    ("U26", "恢复失败"): "无持久化、无跨会话状态；重启即空，不需要恢复流程。",

    # ── U27 AppPaths ──
    ("U27", "缺失"): "6 个路径全部解析并暴露；缺的是「基准目录可配」这一环，已登在 F-A09。",
    ("U27", "延迟"): "构造期 5~6 次 File.Exists/Directory.Exists，微秒级，一次性。",
    ("U27", "重复"): "同一配置只解析一次，结果只读属性，不会两次得到不同路径。",
    ("U27", "失败"): "解析本身不抛（配置来自文件，非法字符才会让 GetFullPath 抛）；写失败发生在消费方。",
    ("U27", "阻塞"): "纯字符串与路径拼接；Exists 是同步元数据查询，微秒级。",
    ("U27", "崩溃"): "无外部依赖、无 IO 写；GetFullPath 对非法字符抛的 ArgumentException 在启动 try 内被接住。",
    ("U27", "资源耗尽"): "只存 6 个字符串。",
    ("U27", "恢复失败"): "无状态机，一次解析即定终身（重启才重算），没有可恢复的中间态。",

    # ── U28 ToolFactory ──
    ("U28", "无效"): "参数（threshold/areaMin/excludeRadius）都有 GetValueOrDefault 默认值，缺键不会得到 0；未知 Type 的形态登在 F-A07。",
    ("U28", "延迟"): "创建是内存构造，唯一 IO 是 ResolveModelPath 的 8 级目录探测（微秒级）。",
    ("U28", "重复"): "CreateAll 按配方顺序各建一次；同名工具会各建一个（结果字典按名覆盖属 Core 侧口径）。",
    ("U28", "阻塞"): "无锁、无等待；模型加载是懒建，创建期不做推理。",
    ("U28", "崩溃"): "构造函数不读模型文件、不抛；模型损坏的形态登在 F-A15。",
    ("U28", "资源耗尽"): "不持句柄；ONNX 会话由工具自己持有并在其 Dispose 释放（Devices/Core 切片已审）。",
    ("U28", "恢复失败"): "无状态，失败即返回 null，下一次启动重新尝试。",

    # ── U29 CameraFactory ──
    ("U29", "缺失"): "三种相机都能创建；模式别名（大小写/缩写）不支持，形态已登在 F-A06。",
    ("U29", "延迟"): "FileCamera 构造只存路径（扫描在 open/RefreshFileList），Hik 打开发生在 SwappableCamera 装配时。",
    ("U29", "重复"): "每次调用 new 一个新实例，旧实例由 SwappableCamera 释放，不会双份存活。",
    ("U29", "失败"): "构造函数不打开设备，失败延后到 open；Camera:Loop 非 true/false 时 bool.Parse 抛 FormatException，经启动 catch。",
    ("U29", "阻塞"): "switch 内只有 new，无 IO 等待。",
    ("U29", "崩溃"): "畸形配置（Camera:Loop=yes）会让 bool.Parse 抛异常 → 启动 catch → Shutdown(1)，不会静默。",
    ("U29", "资源耗尽"): "不持句柄，设备句柄归具体相机实现。",
    ("U29", "恢复失败"): "无状态；相机不可用的恢复入口是 SwappableCamera.SwapTo 与相机页重连。",

    # ── U30 ServiceCollectionExtensions ──
    ("U30", "缺失"): "五组注册齐全；缺的是「注册结果与配置一致性自检」（如 ITriggerSource 是否对得上 Trigger:Mode），登在 F-A13。",
    ("U30", "延迟"): "注册只登记委托，对象在首次解析时构造；启动期一次性。",
    ("U30", "重复"): "每组 Add 只被调用一次；重复调用会重复登记（后者生效），当前调用点唯一。",
    ("U30", "失败"): "配置解析用裸 int.Parse/long.Parse，畸形值抛 FormatException → 启动 catch → Shutdown(1)，属 fail-fast。",
    ("U30", "阻塞"): "无 IO、无等待；ReadHousekeepingOptions 只做字符串解析。",
    ("U30", "崩溃"): "同上，异常在启动 try 内被接住，不会留下半初始化的容器。",
    ("U30", "资源耗尽"): "只登记委托与单例描述符，不持有实例。",
    ("U30", "恢复失败"): "无状态；启动失败后由看门狗重启，整个容器重建。",

    # ── U31 MainWindow ──
    ("U31", "无效"): "导航按页面名分发、两页互斥；编辑态与页面互斥时的缺口已登在 F-A08。",
    ("U31", "延迟"): "Visibility 切换只触发一次布局失效，无动画、无等待。",
    ("U31", "重复"): "NavigateMessage 每次点击发一条；重复点同一项只是重复设置同一 Visibility。",
    ("U31", "失败"): "控件查找与 Visibility 赋值不会失败；DataContext 为 null 时绑定静默不显示，属 WPF 语义。",
    ("U31", "阻塞"): "无同步等待；Show() 之后不再阻塞消息循环。",
    ("U31", "崩溃"): "构造期只做依赖赋值与消息注册；子控件由 DI 提前实例化，构造失败在启动阶段就暴露。",
    ("U31", "资源耗尽"): "一个消息订阅（窗口为单例，应用生命周期内不注销）；不持句柄。",
    ("U31", "恢复失败"): "无持久化状态；_isEditMode 只在内存，重启归零。",

    # ── U32 ImagePanelView ──
    ("U32", "缺失"): "缩放/平移/复位/坐标读数都有入口；缺的是「新图到达时复位」，已登在 F-A11。",
    ("U32", "延迟"): "MouseMove 里一次除法加一次字符串赋值，60Hz 下无感。",
    ("U32", "重复"): "每次滚轮改一次 _zoom 并 ApplyZoom，幂等；重复按 F 只是重复设 1.0。",
    ("U32", "失败"): "图像为空时走占位分支（(—, —)），不会因 ActualWidth=0 得到 Infinity。",
    ("U32", "阻塞"): "事件处理器无 IO、无等待、无锁。",
    ("U32", "崩溃"): "除零已由 dispW>0 && imgW>0 守卫；CaptureMouse/ReleaseMouseCapture 成对，异常路径不涉及。",
    ("U32", "资源耗尽"): "只持 Transform 数值；BitmapSource 由 VM 持有，View 不复制像素。",
    ("U32", "恢复失败"): "无跨会话状态；重启后缩放回到 1.0。",

    # ── E18 触发源 → U22 ──
    ("E18", "无效"): "OnTriggered 按 CheckAccess 分流到 UI 线程；退出期 Dispatcher 已关时 InvokeAsync 抛 TaskCanceled，不影响判定。",
    ("E18", "缺失"): "触发事件只带「来了一帧」一个信息，来源标记被 MainViewModel 写死 \"Manual\"（定时器触发也记 Manual）——记录口径问题，未达故障。",
    ("E18", "重复"): "TimerTrigger 每个周期只 Fire 一次；手动与定时叠加时由 _isProcessing 去重。",
    ("E18", "失败"): "处理器是 async void，内部异常由 ProcessFrame 的 try 覆盖；订阅者只有一个。",
    ("E18", "阻塞"): "InvokeAsync 是排队不阻塞；Dispatcher 忙时触发堆在队列里（丢弃形态登在 O-A01）。",
    ("E18", "崩溃"): "async void 的异常若逃出 ProcessFrame 会进 DispatcherUnhandledException；ProcessFrame 全 catch，当前无逃逸路径。",
    ("E18", "资源耗尽"): "无队列、无缓冲；每帧一次委托调用。",
    ("E18", "恢复失败"): "无状态；下一帧重新触发即可。",

    # ── E19 U22 → Camera ──
    ("E19", "无效"): "Capture 返回 null 时 MainViewModel 显式记 Warning 并发「无图像」消息，不继续往下跑。",
    ("E19", "缺失"): "CameraFrame 三个字段都有消费方；Hik 模式下 FilePath 是占位名，脱钩问题已由 Storage F-S01 处理。",
    ("E19", "延迟"): "HikCamera 在锁内只做一次 Buffer.BlockCopy（~4MB，百微秒级），无新帧直接返回 null，不等待。",
    ("E19", "重复"): "每帧只 Capture 一次；同一帧不会被两个消费方取走（_hasNewFrame 消费即置 false）。",
    ("E19", "失败"): "设备掉线由 IsAvailable 的心跳判据体现，Capture 返回 null 而不是抛异常。",
    ("E19", "阻塞"): "锁粒度是「拷贝快照」，不在锁内解码；与 GrabLoop 互斥但都短。",
    ("E19", "崩溃"): "SDK 调用点均在 try 内（Devices 切片已审）；RawToMat 失败记日志并返回 null。",
    ("E19", "恢复失败"): "无会话状态需要恢复；相机重连由 SwappableCamera 与控制面板负责。",

    # ── E20 U22 → Pipeline ──
    ("E20", "无效"): "报告字段（Id/Timestamp/TriggerSource/ImagePath/Results）由 Pipeline 构造；工具集不完整的形态登在 F-A07。",
    ("E20", "延迟"): "同步执行全部工具，单帧 100~300ms 直接计入 CycleTime，属预期成本。",
    ("E20", "重复"): "一帧调用一次 Run，报告每帧新建、Guid 唯一。",
    ("E20", "失败"): "单个工具异常被 Pipeline 转成 ToolResult.Ng，不中断后续工具。",
    ("E20", "阻塞"): "无锁；Pipeline 内部只做 OpenCV 同步运算。",
    ("E20", "崩溃"): "Pipeline 对每个工具 try/catch，异常不外逃到帧处理。",
    ("E20", "资源耗尽"): "内部 gray 用 using 释放；不持有输入 Mat 的所有权（所有权边界正是 F-A02 指的那处）。",
    ("E20", "恢复失败"): "纯函数式，一次调用一个报告，无中间态。",

    # ── E21 U22 → 持久化 ──
    ("E21", "延迟"): "同步存图 + 一次 DB 往返，单帧 10~30ms，已含在 CycleTime 内。",
    ("E21", "重复"): "一帧一次 Save、一次 SaveAsync；文件名带 report.Id 前缀后不再覆盖（Storage F-S05 已修）。",
    ("E21", "失败"): "存图失败有连续 10 次升级告警；落库失败被外层 catch 吞成「检测异常」，登在 F-A03。",
    ("E21", "阻塞"): "SQLite 写-写串行已由 Storage F-S04（WAL + 5s 超时）处理，本层不重复。",
    ("E21", "崩溃"): "两处 IO 都在帧处理的 try 内；ImageStoreService 自己把 IO 异常转成 Failed。",
    ("E21", "资源耗尽"): "图片按日期分目录 + 管家按保留天数清理；数据库按 CreatedAt 清理（Storage/Service 切片已审）。",
    ("E21", "恢复失败"): "每帧独立，失败不影响下一帧重试。",

    # ── E22 U22 → 面板 ──
    ("E22", "无效"): "四类消息各自单一语义；ResultPanel 的行判定与 DefectTool 同源（total = scratch + notch），行与总判定一致。",
    ("E22", "延迟"): "消息分发是同步委托调用，微秒级。",
    ("E22", "重复"): "一帧一条结果消息；同帧的 LogMessage 走另一条通道，不算重复。",
    ("E22", "失败"): "订阅者处理器无返回值、无 IO；异常会直接抛回发送方（在帧处理 try 内）。",
    ("E22", "崩溃"): "订阅者是单例 VM，构造期注册；处理器内只做属性赋值与集合操作，且在 UI 线程。",
    ("E22", "资源耗尽"): "WeakReferenceMessenger 持弱引用，不阻止 VM 回收。",
    ("E22", "恢复失败"): "无状态同步问题；面板内容由下一帧覆盖。",

    # ── E23 View → VM ──
    ("E23", "缺失"): "按下/移动/抬起/右键删除都接到 VM；缺的是鼠标捕获（移出控件后丢 MouseUp），与 O-A06 同属坐标合法性问题。",
    ("E23", "延迟"): "事件处理器直调 VM，无节流但无 IO。",
    ("E23", "重复"): "同一次拖拽的多个 MouseMove 逐次更新同一 ROI，幂等。",
    ("E23", "失败"): "VM 方法不返回值、不抛（坐标换算有 0 守卫）；失败表现为坐标不更新。",
    ("E23", "阻塞"): "无锁、无等待，只在 UI 线程执行。",
    ("E23", "崩溃"): "处理器内无 IO、无外部调用；唯一会抛的是保存命令（属 F-A04）。",
    ("E23", "资源耗尽"): "无分配（Point 是结构体）。",
    ("E23", "恢复失败"): "无持久状态机；拖拽状态在下一次 MouseDown 重置。",

    # ── E24 U23 → 配方 → U22 ──
    ("E24", "无效"): "保存后 MainViewModel 从同一个 RecipeConfig 实例全量重载 _rois，内存与文件一致；文件损坏的恢复登在 F-A14。",
    ("E24", "缺失"): "RoisUpdatedMessage 只通知「变了」不带差异——MainViewModel 全量重载，语义足够。",
    ("E24", "延迟"): "写盘与消息同步完成，几十 KB 文件微秒级。",
    ("E24", "重复"): "保存一次发一条消息；ROI 全量替换而非追加。",
    ("E24", "失败"): "保存失败的形态登在 F-A04（无 catch、非原子），此处不重复。",
    ("E24", "阻塞"): "同步文件写，无锁、无队列。",
    ("E24", "资源耗尽"): "不持写句柄（WriteAllText 内部开关）。",
    ("E24", "恢复失败"): "损坏配方的恢复动作本身有问题（回退默认配方），登在 F-A14。",

    # ── E25 Sidebar → MainWindow ──
    ("E25", "无效"): "页面名是字符串字面量，侧边栏与 MainWindow 各自写死；三处当前一致，拼错会表现为白屏，留待本地化时统一。",
    ("E25", "延迟"): "一次消息分发 + 两次 Visibility 赋值。",
    ("E25", "重复"): "同一导航项可反复点，重复设置同一 Visibility。",
    ("E25", "失败"): "无 IO、无返回值；页面名不匹配时静默不切页。",
    ("E25", "阻塞"): "同步委托，无等待。",
    ("E25", "崩溃"): "处理器只操作控件可见性；控件在 MainWindow 构造期已赋值。",
    ("E25", "资源耗尽"): "一个消息订阅（窗口级单例）。",
    ("E25", "恢复失败"): "无持久化页面状态；重启回到检测页。",

    # ── E26 App → HealthChecker ──
    ("E26", "缺失"): "五项检查（设备/数据库/存储/模板/配方）齐全；缺的是「结论到动作」的接线，登在 F-A01。",
    ("E26", "延迟"): "五次检查含两次写探测与一次设备统计，毫秒级，启动一次性。",
    ("E26", "重复"): "启动只跑一轮；重复跑不改变系统状态（探测文件写后即删）。",
    ("E26", "失败"): "单项检查异常被 Safe 包成该项 FAIL，不中断其余检查（F-007 已修）。",
    ("E26", "阻塞"): "检查内部是同步 IO/SDK 调用，总耗时 <100ms；相机连接统计不等待硬件。",
    ("E26", "崩溃"): "全部经 Safe 包裹，检查异常不会终止启动。",
    ("E26", "资源耗尽"): "探测文件即写即删，不留残留。",
    ("E26", "恢复失败"): "自检本身不做修复（设计如此）；失败后系统是否该降级无人负责，登在 F-A01。",
}


def main():
    scope = json.load(open(SCOPE, encoding="utf-8"))
    targets = scope["targets"]
    grid = {(t, c) for t in targets for c in CATS}

    covered = set()
    refs = {}          # path:line -> [id]，同一行两条 = 重复计数
    for e in ENTRIES:
        eid = e["id"]
        ref = e["code_ref"].split("@")[0]
        refs.setdefault(ref, []).append(eid)
        covered.add((e["target"], e["category"]))
        for i in e.get("instances", []) or []:
            covered.add((i["target"], i["category"]))

    dup = {k: v for k, v in refs.items() if len(v) > 1}
    if dup:
        raise SystemExit(f"同一 path:line 被登记多次（重复计数）: {dup}")

    missing = grid - covered
    no_reason = [k for k in missing if k not in NA]
    unused = [k for k in NA if k not in missing]
    if no_reason:
        raise SystemExit(f"还有 {len(no_reason)} 格没有判定: {sorted(no_reason)}")
    if unused:
        raise SystemExit(f"NA 表有多余键（会造成重复覆盖，或格已被 FOUND 占）: {sorted(unused)}")

    entries = list(ENTRIES)
    for t, c in sorted(NA):
        entries.append({
            "id": f"N-{t}-{CATS.index(c) + 1}",
            "target": t,
            "category": c,
            "status": "N/A",
            "na_reason": NA[(t, c)],
        })

    main_n = sum(1 for e in ENTRIES if e.get("tier") == "MAIN")
    obs_n = sum(1 for e in ENTRIES if e.get("tier") == "OBSERVATION")
    doc = {
        "meta": {
            "slice": "app",
            "grid": len(grid),
            "found": len(ENTRIES),
            "main": main_n,
            "observation": obs_n,
            "na": len(NA),
            "targets": targets,
            "generated_by": "audit/scripts/gen_app_stage1.py",
            "note": "同一「目标×类别」允许多条 FOUND（不同 path:line 即不同根因）；同一 path:line 只许一条。",
        },
        "entries": entries,
        "category_exemptions": [],
    }
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, indent=2)
    print(f"写出 {OUT}")
    print(f"  网格 {len(grid)} 格 = FOUND {len(ENTRIES)} 条（MAIN {main_n} / OBS {obs_n}）+ N/A {len(NA)} 格，缺 0")


if __name__ == "__main__":
    main()
