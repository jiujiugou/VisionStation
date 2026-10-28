using System.IO;
using VisionStation.Core.Models;
using VisionStation.Devices;
using VisionStation.Service.Recipes;

namespace VisionStation.Service;

/// <summary>
/// 启动自检 —— 五件事，一次跑完。
///
/// <para>
/// <b>定位</b>：启动链路的第三环（连设备 → <b>自检</b> → 建库 → 起 VM 监听）。
/// 唯一的调用方是 <c>AppHost.InitializeAsync</c>：它先连设备，再调本类，
/// 把 5 条结果逐条写进日志（OK 记 Information、FAIL 记 Error），
/// 最后交给界面汇总成状态栏一行（<c>HealthSummary.Describe</c>）。
/// 本类不读配置文件：库路径、存储目录、配方、资源根全部由组合根的 DI 工厂算好传进来 ——
/// Service 层只负责"检查这个行为"，路径是怎么来的不归它管。
/// </para>
///
/// <para>
/// <b>五项检查，以及各自挂掉代表什么现场问题</b>：
/// <list type="number">
///   <item><b>设备连接</b>（<see cref="CheckDevices"/>）—— 注册的设备是否全部在线。
///         相机层删除后 <c>DeviceManager</c> 里只剩"触发源"一个设备（PLC 或定时器），
///         所以这一项失败基本只有两种含义：装配漏注册（0 台）或触发源没连上。
///         <b>它不再等价于"相机掉线"</b> —— 相机由 VM 独占，本程序不知道相机在不在。</item>
///   <item><b>数据库</b>（<see cref="CheckDatabase"/>）—— 库文件是不是真的 SQLite、能不能读写打开、
///         不存在时父目录能不能写。失败 = 这一趟跑下来的所有判定都记不下来，最严重的一项。</item>
///   <item><b>存储路径</b>（<see cref="CheckStorage"/>）—— 目录能不能建、能不能写。
///         失败 = 数据根不可写，日志/库/配方保存会接连出事。</item>
///   <item><b>检测引擎</b>（<see cref="CheckEngine"/>）—— 配方声明的 .sol 流程文件在不在、是不是空文件，
///         外加组合根探测出的"运行时不可用"（当前 = VM 结果推送入口立不起来，见 <see cref="_engineRuntimeProblem"/>）。
///         失败 = 换型时流程文件没拷过来、路径写错（部署问题，不是代码问题），或推送链路起不来（配置/占用问题）。</item>
///   <item><b>配方</b>（<see cref="CheckRecipe"/>）—— 调 <c>RecipeConfig.Validate()</c> 查参数合法性。
///         失败 = 引擎参数/输出映射写错，程序会"跑起来但判据是错的"。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>依赖顺序</b>：五项之间**互不依赖**，各读各的输入，重排也不影响结果。
/// 真正的顺序约束在调用方：设备连接必须早于自检（本类只读 <c>ConnectionStats</c>，不负责连接），
/// 否则第 1 项永远报 0/N —— 这也是 <c>AppHost</c> 把那两步写死成先后关系的原因。
/// </para>
///
/// <para>
/// <b>为什么这里一条 MessageBox 都没有</b>：本类跑在启动线程（UI 线程）上，
/// 此刻界面还没建出来，而看门狗在等主程序的就绪信号。弹一个模态框，
/// 启动线程就停在"等人点确定"上，看门狗等不到信号会判启动失败并重启进程 ——
/// 现场看到的是"程序反复弹窗又自己消失"，比不提示更难查。
/// 所以失败一律**只返回结果 + 记日志**，提示由界面在起来之后补上。
/// </para>
///
/// <para>
/// <b>失败行为</b>：永不抛。单项检查里抛出的异常会被 <see cref="RunAll"/> 的 Safe 包成该行 FAIL，
/// 其余各项照常返回 —— 一个探针坏掉不该让整份自检结果拿不到。
/// </para>
/// </summary>
public class HealthChecker : IHealthChecker
{
    /// <summary>SQLite 文件头魔数（"SQLite format 3\0"，16 字节）</summary>
    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    /// <summary>设备管理器。只读它的连接状态，本类**不发起连接**（连接是 AppHost 的事）。</summary>
    private readonly DeviceManager _devices;

    /// <summary>SQLite 库文件的绝对路径（由组合根算好，本类不拼路径）。</summary>
    private readonly string _dbPath;

    /// <summary>
    /// 存储目录（= <c>Storage:ImageFolder</c> 解析结果，默认 <c>&lt;数据根&gt;/InspectedImages</c>）。
    ///
    /// <para>
    /// 注意现状：相机层删除后**本程序不写图**（图由 VM 存在它自己的输出目录），
    /// 这个目录当前没有生产写入方。于是第 3 项检查实际上退化成"数据根可不可写"的探针 ——
    /// 它仍然值得跑（数据根不可写会连带毁掉库与日志），但别把它读成"图片归档正常"。
    /// </para>
    /// </summary>
    private readonly string _storagePath;

    /// <summary>已加载的配方。可空是刻意的：探针测试只验单项检查时不必造一份完整配方。</summary>
    private readonly RecipeConfig? _recipe;

    /// <summary>引擎资源的解析基准（exe 目录）。配方里的 .sol 相对路径按它解析。</summary>
    private readonly string _assetRoot;

    /// <summary>
    /// 检测引擎"跑不起来"的原因（组合根探测后传进来），null = 正常。
    ///
    /// <para>
    /// 探测什么、为什么这么探，全部由组合根决定，这里只负责把结论说给操作员听
    /// （本类不读配置、不碰网络，见类注释的第一段）。
    /// </para>
    /// <para>
    /// 现状（F-49 起）：组合根传的是"VM 结果推送入口能不能立起来"——
    /// 它是推送驱动引擎的运行时前置。<b>不要</b>在这里补一条"本机装没装 VisionMaster"的探测：
    /// 本程序不加载 VM 的 SDK，那条永远是绿的（假绿灯比没有更糟）。
    /// </para>
    /// <para>
    /// 传 null 的场合仍然存在（非 Vm 引擎、或组合根判断无可探的对象），
    /// 此时这一段分支不生效，检查退化成"文件在不在、是不是空文件"两条 —— 这在本类里是已知行为，
    /// 测试也覆盖了传参路径（<c>R103_EngineRuntimeMissing_MustFail</c>）。
    /// </para>
    /// </summary>
    private readonly string? _engineRuntimeProblem;

    /// <param name="devices">设备管理器</param>
    /// <param name="dbPath">数据库文件（绝对路径）</param>
    /// <param name="storagePath">存储目录（绝对路径；当前无生产写入方，见字段说明）</param>
    /// <param name="recipe">已加载的配方（可空：部分探针测试只验单项检查）</param>
    /// <param name="assetRoot">引擎资源的解析基准目录（exe 目录）</param>
    /// <param name="engineRuntimeProblem">检测引擎运行时不可用的原因（可选；组合根探测后传入，见字段说明）</param>
    public HealthChecker(DeviceManager devices,
        string dbPath, string storagePath, RecipeConfig? recipe, string assetRoot,
        string? engineRuntimeProblem = null)
    {
        _devices = devices;
        _dbPath = dbPath;
        _storagePath = storagePath;
        _recipe = recipe;
        _assetRoot = assetRoot;
        _engineRuntimeProblem = engineRuntimeProblem;
    }

    /// <summary>
    /// 执行所有检查，返回结果列表。
    ///
    /// <para>
    /// 单项检查抛出的异常在这里被就地转成该项 FAIL（<c>Safe</c>）——
    /// 一个设备坏了不该让整份自检结果拿不到，那会让 AppHost 少记四条日志，
    /// 现场就只剩"什么都没有"。
    /// </para>
    /// <para>
    /// 顺序固定为 设备 → 数据库 → 存储 → 引擎 → 配方：彼此不依赖，但日志按这个顺序读最顺
    /// （先看硬件通不通，再看数据能不能落，最后看配置对不对）。
    /// 调用方是启动线程，本方法只读现场状态、只写一处探针文件，不产生业务副作用。
    /// </para>
    /// </summary>
    public List<HealthResult> RunAll()
    {
        static HealthResult Safe(Func<HealthResult> check, string item)
        {
            try { return check(); }
            catch (Exception ex)
            {
                // 这里刻意只留类型名+消息：自检跑在启动线程上，任何等待/弹窗都会拖住启动。
                return new HealthResult(item, false, "检查异常",
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        return new()
        {
            Safe(CheckDevices, "设备连接"),   // 1. 设备（相机层删除后 = 触发源：PLC / 定时器）
            Safe(CheckDatabase, "数据库"),    // 2. 数据库文件（能不能真写进去）
            Safe(CheckStorage, "存储路径"),   // 3. 存储路径（当前无生产写入方，见字段说明）
            Safe(CheckEngine, "检测引擎"),    // 4. 引擎资源（.sol 流程文件）
            Safe(CheckRecipe, "配方")         // 5. 配方（"配方"仅用于异常兜底，正常路径叫"配方文件"）
        };
    }

    // ════════════════════════════════════════════════
    //  检查项
    // ════════════════════════════════════════════════

    /// <summary>
    /// 第 1 项：设备是否全部在线。
    ///
    /// <para>
    /// 读的是 <c>DeviceManager</c> 的连接统计，**不触发连接** —— 连接由 AppHost 在自检之前做完，
    /// 这里只负责如实报告结果。把连接塞进自检会让"谁先跑"决定硬件是否被打开，
    /// 测试一建容器就会去开真设备。
    /// </para>
    /// <para>
    /// 现状（要读代码才知道的那部分）：<c>DeviceManager</c> 现在只注册一个
    /// <c>TriggerDevice</c>，而它的 <c>IsConnected</c> 是硬编码 <c>true</c>
    /// （手工/定时触发没有"连接"概念）。也就是说这一项目前恒为通过，
    /// 它真正能抓到的只有"装配漏注册"（total == 0）这一种事故；
    /// PLC 掉线不会让这里变红，只会体现为 <c>Status</c>/<c>LastError</c> 里的文字。
    /// 别把"自检通过"当成"产线通讯正常"。
    /// </para>
    /// </summary>
    private HealthResult CheckDevices()
    {
        var (online, total) = _devices.ConnectionStats;
        // 空设备表不能判「全部在线」：0==0 会让判据失效，必须要求 total > 0。
        bool ok = total > 0 && online == total;
        if (ok)
            return new HealthResult("设备连接", true, $"{online}/{total} 在线");

        if (total == 0)
            return new HealthResult("设备连接", false, "0/0 未注册任何设备", "未注册任何设备");

        // 离线时把每台的原因带出来，而不是一句"有设备离线"——
        // "目录不存在"和"设备被独占"的排查方向完全不同，笼统的离线会把人引向查线。
        var reasons = _devices.Devices
            .Where(d => !d.IsConnected)
            .Select(d => $"{d.Name}: {d.LastError ?? "离线（未记录原因）"}");
        return new HealthResult("设备连接",
            false,
            $"{online}/{total} 在线",
            string.Join("；", reasons));
    }

    /// <summary>
    /// 第 2 项：数据库可用性。文件存在时要同时满足"是真 SQLite"和"能读写打开"两条；
    /// 文件不存在时不判失败 —— SQLite 首次运行会自己建库，缺文件是正常的，
    /// **父目录不可写才是故障**。
    /// </summary>
    private HealthResult CheckDatabase()
    {
        if (File.Exists(_dbPath))
        {
            // 文件存在 ≠ 可用。两件事必须实测：
            //   1) 文件头是 SQLite 魔数（挡住被覆盖成文本/半截文件）
            //   2) 能以读写方式打开（挡住被其他进程独占 —— 检测记录写不进去）
            try
            {
                var head = new byte[16];
                int n;
                using (var fs = new FileStream(_dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    n = fs.Read(head, 0, head.Length);

                if (n < 16 || !head.AsSpan().SequenceEqual(SqliteMagic))
                    return new HealthResult("数据库", false, _dbPath, "文件存在但不是有效的 SQLite 库");
            }
            catch (Exception ex)
            {
                return new HealthResult("数据库", false, _dbPath, $"数据库文件无法读取（{ex.GetType().Name}）");
            }

            try
            {
                using var _ = new FileStream(_dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch (Exception ex)
            {
                return new HealthResult("数据库", false, _dbPath, $"数据库文件被占用（{ex.GetType().Name}）");
            }

            return new HealthResult("数据库", true, _dbPath);
        }

        // 库文件不存在时探测父目录可写
        // （SQLite 首次运行自动建库，所以「缺文件」本身不是故障，「目录不可写」才是）。
        var dir = Path.GetDirectoryName(_dbPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return new HealthResult("数据库", false, _dbPath, "数据库路径不可达");

        try
        {
            var probe = Path.Combine(dir, ".db_write_test");
            File.WriteAllText(probe, "t");
            File.Delete(probe);
            return new HealthResult("数据库", true, _dbPath);
        }
        catch
        {
            return new HealthResult("数据库", false, _dbPath, "数据库目录不可写");
        }
    }

    /// <summary>
    /// 第 3 项：存储目录是否可创建、可写。
    ///
    /// <para>
    /// 目录不存在就顺手建（自检是唯一允许在这里做写操作的地方：它本来就是探针）。
    /// 写一个 <c>.write_test</c> 再删掉，是为了绕开"目录存在但权限不足"这种
    /// 光看 <c>Exists</c> 查不出来的情况。
    /// </para>
    /// <para>
    /// 这个文件名不是随手起的：<c>HousekeepingService</c> 的清理会跳过 <c>.write_test</c> /
    /// <c>.db_write_test</c>，否则探针文件自己会被当成过期图片删掉，
    /// 或者在"保留期设为 0 天"的极端配置下把探针删了却报成功。
    /// </para>
    /// <para>
    /// 现状提醒：这个目录当前没有生产写入方（图片由 VM 存），失败时的真实含义是
    /// "数据根不可写"—— 那会连带毁掉日志、数据库和配方保存，所以仍然要报。
    /// </para>
    /// </summary>
    private HealthResult CheckStorage()
    {
        if (!Directory.Exists(_storagePath))
        {
            try { Directory.CreateDirectory(_storagePath); }
            catch { return new HealthResult("存储路径", false, _storagePath, "无法创建目录"); }
        }

        try
        {
            var testFile = Path.Combine(_storagePath, ".write_test");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return new HealthResult("存储路径", true, _storagePath);
        }
        catch
        {
            return new HealthResult("存储路径", false, _storagePath, "目录不可写");
        }
    }

    /// <summary>
    /// 第 4 项：检测引擎检查：引擎资源在不在、运行时有没有缺。
    ///
    /// 这三件事分别对应三种完全不同的故障：
    ///   1. 资源缺失 —— 工程师没把 .sol / 模型拷过来（部署问题）
    ///   2. 运行时缺失 —— 本机没装 VM 或没部署其运行时（环境问题）
    ///   3. 配方非法 —— 引擎名/参数写错（配置问题）
    /// 混成一句"检测失败"的话，现场要在三种方向之间乱试。
    ///
    /// <para>
    /// 现状：运行时只有 <c>Vm</c> 一个引擎（ONNX 已于 2026-10-27 整体删除）。
    /// 所以这里只检查 VM 的 <c>.sol</c>；配方声明别的引擎名时，组合根建引擎就会直接抛
    /// （<c>InspectionEngineFactory</c>：不静默换引擎），程序起不来，根本走不到自检。
    /// </para>
    /// <para>
    /// 判据顺序也是刻意的：先文件、再空文件、最后才报运行时缺失 ——
    /// 文件都没有的时候谈运行时没有意义，先说最靠前的那一步。
    /// </para>
    /// </summary>
    private HealthResult CheckEngine()
    {
        if (_recipe is null)
            return new HealthResult("检测引擎", false, "未加载配方", "配方为空，无法确定用哪个引擎");

        var label = "VM 流程文件";
        var configured = _recipe.Vm.SolutionPath;
        if (string.IsNullOrWhiteSpace(configured))
            return new HealthResult("检测引擎", false, _recipe.Engine, $"{label}路径未配置");

        var path = RecipeConfig.ResolveAsset(configured, _assetRoot);

        if (!File.Exists(path))
            return new HealthResult("检测引擎", false, $"{_recipe.Engine} · {path}",
                $"{label}不存在");

        try
        {
            if (new FileInfo(path).Length == 0)
                return new HealthResult("检测引擎", false, $"{_recipe.Engine} · {path}", $"{label}是空文件");
        }
        catch (IOException ex)
        {
            return new HealthResult("检测引擎", false, $"{_recipe.Engine} · {path}", $"文件无法读取（{ex.GetType().Name}）");
        }

        // F-68 契约校验（2026-10-28）：配方按名字引用的模块必须真的在方案名册里。
        // 放在"文件存在"之后、"运行时"之前——文件都没有时谈名册没有意义；
        // 而契约断了比运行时缺失更隐蔽：运行时缺了会没结果，契约断了会**错结果**。
        // 失败不阻止程序启动（还能进界面修配置），但联锁会凭这条自检失败拦住自动运行。
        var contractProblems = RecipeContractValidator.Validate(_recipe, path);
        if (contractProblems.Count > 0)
            return new HealthResult("检测引擎", false, $"{_recipe.Engine} · {Path.GetFileName(path)}",
                string.Join("；", contractProblems));

        // 运行时缺失优先报：此时即使文件齐了也跑不起来，先说最致命的那条
        if (_engineRuntimeProblem is { } problem)
            return new HealthResult("检测引擎", false, $"{_recipe.Engine} · {Path.GetFileName(path)}", problem);

        return new HealthResult("检测引擎", true, $"{_recipe.Engine} · {Path.GetFileName(path)}");
    }

    /// <summary>
    /// 第 5 项：配方自身的合法性（与画面尺寸无关）。
    ///
    /// <para>
    /// 校验规则一条都不在这里写，全部委托给 <c>RecipeConfig.Validate()</c> ——
    /// 规则写在两处迟早会对不上，这是"单一真相源"最容易被破坏的地方。
    /// 本方法只负责把问题清单翻成一句人话（最多取 3 条，长了状态栏放不下）。
    /// </para>
    /// <para>
    /// 注意项名有两套：正常路径返回的 Item 是 <b>"配方文件"</b>，
    /// 而 <see cref="RunAll"/> 的异常兜底传的是 <b>"配方"</b>。
    /// 界面上会看到两种写法，按名字过滤的调用方（含契约测试）得知道这件事。
    /// </para>
    /// </summary>
    private HealthResult CheckRecipe()
    {
        if (_recipe == null)
            return new HealthResult("配方文件", false, "空", "配方为空");

        // 校验规则本身在 Core（RecipeConfig.Validate），这里只负责把它说给操作员听。
        // 规则写在两处迟早会对不上 —— 这是"单一真相源"最容易被破坏的地方之一。
        var problems = _recipe.Validate();
        bool ok = problems.Count == 0;

        return new HealthResult("配方文件",
            ok,
            ok ? $"{_recipe.Name} · {_recipe.Engine}" : string.Join("；", problems.Take(3)),
            ok ? null : "配方不合法");
    }
}

/// <summary>
/// 单条自检结果（**这一份才是生产在用的**）。
///
/// <para>
/// 归属：纯数据、不可变，每次自检新建 5 个，生命周期不超过一次启动。
/// 由 <see cref="HealthChecker"/> 产出，交给 <c>AppHost</c> 记日志 + 存进
/// <c>AppHost.HealthResults</c>，再被界面汇成一行状态文字。没有任何写库/写文件行为。
/// </para>
/// <para>
/// 四个字段的分工：<see cref="Item"/> 决定"归哪一类故障"，<see cref="Detail"/> 给证据（路径、在线数），
/// <see cref="Error"/> 只在失败时非空且要写清下一步往哪儿查 ——
/// "有设备离线"这种话没有信息量，"目录不存在"和"被独占"才是能行动的原因。
/// </para>
/// <para>
/// 另有一份同名的 <c>VisionStation.Service.HealthCheck.HealthResult</c> 是无人使用的旧草案，
/// 加字段请加在这里。
/// </para>
/// </summary>
public class HealthResult
{
    public string Item { get; }        // "相机"
    public bool OK { get; }            // ✅/❌
    public string Detail { get; }      // "1/2 在线"
    public string? Error { get; }      // null = 没问题

    public HealthResult(string item, bool ok, string detail, string? error = null)
    {
        Item = item; OK = ok; Detail = detail; Error = error;
    }

    public override string ToString()
        => $"[{(OK ? "✅" : "❌")}] {Item}: {Detail}{(Error != null ? $" — {Error}" : "")}";
}
