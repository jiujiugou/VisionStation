using System.IO;

namespace VisionStation.Core.Models;

/// <summary>
/// 一份完整的检测配方 —— 检测一个产品所需的全部配置。
///
/// <para>
/// <b>定位</b>：换型的唯一开关。它决定"跑哪个引擎、引擎的参数在哪、报文各段对应什么业务名"。
/// 上游是磁盘上的 <c>Config/recipes/*.json</c>（见下），下游是
/// <c>InspectionEngineFactory</c>（选引擎）、<c>VmInspectionDriver</c>（分隔符解析时按
/// <see cref="VmRecipeData.Outputs"/> 顺序对号入座）、<c>HealthChecker</c>（启动自检调
/// <see cref="Validate"/>）。
/// </para>
///
/// <para>
/// <b>单一真相源是磁盘</b>：<c>Config/recipes/</c> 下的 json 才是配方本体，
/// 程序集内嵌的 <c>Config/recipes/default.json</c> 只是<b>兜底</b> ——
/// 磁盘上候选文件全部缺失/损坏/为空时，<c>AppHost.LoadRecipe</c> 才回退到它。
/// 现场改配方改的是磁盘那份；改源代码里那份内嵌副本要重新编译才生效，
/// 而且只要磁盘上有合法配方，内嵌的那份根本不会被读。
/// 判断"一个 json 算不算配方"的判据是 <see cref="HasEngineDeclaration"/>，不是文件名。
/// </para>
///
/// <para>
/// <b>归属</b>：组合根启动时加载一次，注册为单例，活到进程结束。
/// <b>运行时没有任何代码替换它</b> —— 换型是"写 .active 指针 + 下次启动加载新的那份"
/// （因为换配方要连带换引擎实例、重载 .sol，不是一次赋值能完成的）。
/// 正因为全程只读，类里没有锁，也没有写时复制。
/// </para>
///
/// <para>
/// <b>失败行为</b>：本类是纯数据 + 纯校验，不抛异常。
/// <see cref="Validate"/> 返回问题清单（空 = 合法），<see cref="CheckSensor"/> 返回原因字符串，
/// <see cref="HasEngineDeclaration"/> 返回 bool。真正的"启动失败"由调用方决定 ——
/// 注意 <c>InspectionEngineFactory</c> 对非 Vm 引擎是<b>直接抛</b>的（见该类的注释），
/// 那是有意的：配方写了运行时没有的东西，宁可起不来。
/// </para>
///
/// <para>
/// schemaVersion 2（2026-10-23）：从"自研工具链"改成"引擎 + 引擎参数"。
///   · 删掉了 rois / tools / calibration 三段 —— ROI 与标定归引擎所有
///     （VM 在 .sol 里配），留在配方里只会造出第二份真相。
///   · 新增 engine 段与 vm 段参数。
///   · 2026-10-27：onnx 段随 ONNX 推理整体删除（运行时从来只跑 VM，那一段只服务
///     已经从仓库移除的离线跑批工具，留着就是第二份没人读的配置）。
/// </para>
///
/// <para>
/// 为什么引擎写在配方里而不是 appsettings：
///   .sol 流程文件与模型文件都是**跟产品绑定**的（换产品就换流程），
///   而 appsettings 描述的是**这台机器**（PLC、存储路径、监听端口）。
///   两者改动的时机完全不同，混在一起就会出现"换配方还要重启改配置"。
/// </para>
///
/// <para>
/// 序列化：System.Text.Json。换产品 = 换一个 json，代码不动。
/// </para>
/// </summary>
public class RecipeConfig
{
    /// <summary>
    /// 当前配方结构版本（改字段含义时靠它做迁移，不是软件版本号）。
    ///
    /// <b>现状</b>：没有任何代码读它做迁移。schema 1 与 2 的实际分界落在
    /// <see cref="HasEngineDeclaration"/> 上（schema 1 没有 engine 段，于是根本不被当成配方）。
    /// 所以别以为改了字段就有兼容逻辑 —— 这个常量目前是"给人看 + 留给将来的迁移"。
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// 配方名称，如 "金属垫圈检测"。
    /// 会出现在启动审计、引擎失败的提示、以及"配方「X」按 1280×1024 制作"这类文案里，
    /// 是现场唯一能用来认配方的东西 —— 所以换型时把它改对，比改文件名有用。
    /// </summary>
    public string Name { get; set; } = "默认配方";

    /// <summary>配方结构版本（读盘时若文件没写这一项，反序列化后就是 <see cref="CurrentSchemaVersion"/>）</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>最近一次保存时间（由配方存储写入；只用于列表展示与"这份是不是刚改过"的判断，不参与任何校验）</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// 本配方使用的检测引擎。大小写不敏感（比较一律 <see cref="StringComparison.OrdinalIgnoreCase"/>）。
    ///
    /// <para>
    /// 默认 <c>Vm</c>，也是**唯一**合法值：ONNX 推理已整体删除（2026-10-27），
    /// 运行时只有 VM 一条路。默认值必须和运行时的实际能力一致 ——
    /// 一份漏填 engine 的配方若悄悄走到一条不存在的路上，现场排查要花一整天。
    /// </para>
    /// <para>
    /// <b>两道判据，职责不同</b>：<see cref="EngineNames.All"/> 只拦"名字都拼错"的配方，
    /// 所以现在写 <c>engine = "Onnx"</c> 会在 <see cref="Validate"/> 这一步就报未知引擎；
    /// <c>InspectionEngineFactory</c> 在启动期再拦一次（不静默回落）。
    /// 两道都留是有意的：配方文件可能被手改，前一道给操作员看，后一道防程序自己走错。
    /// </para>
    /// </summary>
    public string Engine { get; set; } = EngineNames.Vm;

    /// <summary>
    /// 本配方适配的画面尺寸。
    ///
    /// <para>
    /// 为什么必须有：ROI 与标定都死死绑在某个分辨率上。换相机或改了采集分辨率
    /// 而配方没跟着改时，.sol 里的流程会框到零件外面、尺寸会算错，
    /// 但程序**一切看起来正常**——不报错、不崩、只是安静地判错。
    /// </para>
    /// <para>
    /// 未声明（Width/Height 为 0）视为旧配方，不校验。
    /// <b>现状</b>：<see cref="CheckSensor"/> 已经没有生产调用者了（相机层删除后，
    /// 本程序看不到实时画面尺寸，无从比对），只剩单测在钉这条规则。
    /// </para>
    /// </summary>
    public SensorSpec Sensor { get; set; } = new();

    /// <summary>VisionMaster 流程参数（这是配方里唯一生效的一段引擎配置）</summary>
    public VmRecipeData Vm { get; set; } = new();

    /// <summary>
    /// 校验本配方能否用于当前画面。返回 null = 通过；否则返回可直接展示给操作员的原因。
    /// 纯函数、无 IO，便于单测。
    ///
    /// <para>
    /// <b>语义</b>：只比"声明过的尺寸"和"这次拿到的尺寸"，不比别的。
    /// 未声明尺寸的旧配方一律放行（返回 null），因为没法判断它是不是真的不匹配。
    /// </para>
    /// <para>
    /// <b>失败行为</b>：不抛异常，返回的字符串本身就是给人看的完整句子
    /// （含两个尺寸与"已拒绝本帧检测"的结论），调用方直接扔到日志/界面即可。
    /// </para>
    /// <para>
    /// <b>现状</b>：没有生产调用者（相机层删除后本程序拿不到实时帧尺寸），
    /// 只有 <c>RecipeSensorTests</c> 在钉这条规则。保留是因为"配方与画面尺寸绑定"
    /// 这个风险仍然存在，将来若接入帧宽高（哪怕是报文里带的）可以直接复用。
    /// </para>
    /// </summary>
    public string? CheckSensor(int frameWidth, int frameHeight)
    {
        if (!Sensor.IsDeclared) return null;
        if (Sensor.Width == frameWidth && Sensor.Height == frameHeight) return null;

        return $"配方「{Name}」按 {Sensor.Width}×{Sensor.Height} 制作，"
             + $"当前画面 {frameWidth}×{frameHeight}；流程 ROI 与标定都会错位，已拒绝本帧检测";
    }

    /// <summary>
    /// 配方自身的合法性（与画面无关）。返回值是要给人看的问题清单，空 = 合法。
    /// 启动自检用它（<c>HealthChecker</c>），避免"参数填错 → 第一帧才炸，而且炸得很隐晦"。
    ///
    /// <para>
    /// <b>为什么返回清单而不是抛</b>：一次把所有问题列全，工程师改一遍就能过；
    /// 抛第一条异常的做法会让人来回改四五次。启动自检的定位是"报告问题"，
    /// 不是"拒绝启动"。
    /// </para>
    /// <para>
    /// <b>它拦不住的东西</b>：.sol 路径是否真的存在、模块名是否真的对得上、
    /// VM 流程是否真能被加载 —— 这三件都要到运行期才知道。
    /// </para>
    /// </summary>
    public List<string> Validate()
    {
        var problems = new List<string>();

        // 只拦"名字都拼错"的情况（现在合法值只有 Vm）。
        // 不在这里检查 .sol 是否存在、模块名是否对得上 —— 那要读磁盘和 VM 流程，属自检的事。
        if (!EngineNames.All.Contains(Engine, StringComparer.OrdinalIgnoreCase))
            problems.Add($"未知引擎 \"{Engine}\"，可选值：{string.Join(" / ", EngineNames.All)}");

        if (string.Equals(Engine, EngineNames.Vm, StringComparison.OrdinalIgnoreCase))
        {
            // 这四项缺任何一项，VM 链路都会"起来了但永远没结果"：
            // 没 solution 不知道跑什么、没 procedure 不知道跑哪条流程、
            // 没有 outputs 则每帧解析出来都是空结果（空结果 = NG，会被当成全线不良）。
            if (string.IsNullOrWhiteSpace(Vm.SolutionPath))
                problems.Add("Vm 引擎未指定流程文件（vm.solution）");
            if (string.IsNullOrWhiteSpace(Vm.Procedure))
                problems.Add("Vm 引擎未指定流程名（vm.procedure）");
            if (Vm.Outputs.Count == 0)
                problems.Add("Vm 引擎未配置任何输出映射（vm.outputs），检测将没有任何结果项");

            // 重名必须拦：分隔符格式按 Label 建键，同名会互相覆盖，
            // 最终表现是"配方里配了 3 项，界面上只有 2 项"，而且总判定还是对的。
            var duplicated = Vm.Outputs
                .GroupBy(o => o.Label, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);
            foreach (var label in duplicated)
                problems.Add($"Vm 输出 label 重复：{label}（会互相覆盖）");

            // module 只在文档/扫描时用，label 才是结果键；但两者都空就等于这条映射什么都不是
            foreach (var o in Vm.Outputs.Where(o => string.IsNullOrWhiteSpace(o.Module) || string.IsNullOrWhiteSpace(o.Label)))
                problems.Add("Vm 输出映射缺少 module 或 label");
        }
        return problems;
    }

    /// <summary>
    /// 判断一段 JSON 是不是配方。
    ///
    /// <para>
    /// 判据是"有没有显式声明 engine" —— 不是"有没有 tools"（那个字段已经不存在了），
    /// 也不是"能不能反序列化"：任何 JSON 都能反序列化成一个字段全默认的对象，
    /// 那样等于把 config 目录里任何一个 json 都当配方。
    /// 语义上这也是对的：一份说不出自己用哪个引擎的配方，本来就不该被加载。
    /// </para>
    /// <para>
    /// <b>为什么必须两处共用</b>：组合根启动加载（<c>AppHost.LoadRecipe</c>）与
    /// 配方列表（<c>JsonRecipeStore.ReadRecipe</c>）用的是同一个判据。
    /// 各写一份就会出现"列表里能看见、启动时加载不了"这种自相矛盾，
    /// 而排查它要先怀疑文件系统。
    /// </para>
    /// <para>
    /// <b>失败行为</b>：非法 JSON、空串、根不是对象、属性名不匹配，一律返回 false，
    /// <b>不抛</b> —— 它被用在"扫描一整个目录"的循环里，一份坏文件不该让列表整体崩掉。
    /// </para>
    /// </summary>
    public static bool HasEngineDeclaration(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            // 根是数组或字面量时直接排除：配方必须是一个对象
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return false;

            // 大小写不敏感：现场手写 json 时 "Engine" / "engine" 都有
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, nameof(Engine), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch (System.Text.Json.JsonException)
        {
            // 文件正在被编辑器写入时也可能读到这里，返回 false 而不是抛 ——
            // 上层（配方列表 / 启动加载）会继续找下一份候选。
            return false;
        }
    }

    /// <summary>
    /// 把配方里的相对路径解析成绝对路径。
    ///
    /// <para>
    /// 相对路径的基准是 <paramref name="assetRoot"/>（组合根传 exe 目录，与 AppPaths 一致），
    /// **不用 Environment.CurrentDirectory** —— 快捷方式、计划任务、调试器给的 cwd 各不相同，
    /// 基于它找文件必然时灵时不灵。这类 bug 的典型症状是"我这边好好的，现场就是不开机"。
    /// </para>
    /// <para>
    /// <b>边界</b>：空串/空白原样返回空串（不拼成目录本身）；已经是绝对路径就原样返回，
    /// 不做规范化 —— 保留现场写的样子，日志里对得上。
    /// </para>
    /// <para>
    /// <b>副作用</b>：无 IO。<c>Path.GetFullPath</c> 只做字符串展开（会处理 <c>..</c>），
    /// 不检查文件是否存在 —— 存在性由真正去读它的人负责报错。
    /// </para>
    /// </summary>
    public static string ResolveAsset(string configured, string assetRoot)
    {
        if (string.IsNullOrWhiteSpace(configured)) return "";
        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(assetRoot, configured));
    }
}

/// <summary>
/// 引擎名常量 —— 与配方 json 里的字符串一一对应，避免各处手写。
///
/// <para>
/// 现在只有一个值。这不是"为将来扩展留的缝"，是删到只剩一个实现之后的事实记录：
/// 2026-10-27 把 ONNX 推理（引擎、离线跑批工具、模型、数据集、相关测试）整条线删掉了，
/// 因为运行时从来只跑 VM，第二套引擎没有任何生产调用者。
/// </para>
/// </summary>
public static class EngineNames
{
    /// <summary>海康 VisionMaster 流程（运行时唯一可用的引擎）</summary>
    public const string Vm = "Vm";

    /// <summary>
    /// 合法引擎集合（配方校验用）。
    /// 只有一个元素是当前事实，不是"预留扩展点"：ONNX 已于 2026-10-27 整体删除，
    /// 加回第二个引擎必须同时改 <c>InspectionEngineFactory</c> 与自检，否则就是假的扩展性。
    /// </summary>
    public static readonly string[] All = { Vm };
}

/// <summary>
/// 配方绑定的画面尺寸（像素）。
///
/// 目的只有一个：让"配方里的 ROI/标定是按哪个分辨率做的"这件事写下来。
/// 换了分辨率而配方没改，流程会安静地判错 —— 不报错、不崩，只是每件都判成不良或全放行。
/// </summary>
public class SensorSpec
{
    /// <summary>画面宽度（px）。0 = 未声明</summary>
    public int Width { get; set; }

    /// <summary>画面高度（px）。0 = 未声明</summary>
    public int Height { get; set; }

    /// <summary>是否声明过尺寸。旧配方没有这一段，值为 0，此时不参与校验。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDeclared => Width > 0 && Height > 0;
}

/// <summary>
/// VisionMaster 流程参数。
///
/// <para>
/// <b>现状</b>：TCP 中转模式下，本程序<b>不加载 .sol、不运行流程、不喂图</b> ——
/// 流程在 VM 客户端里独立跑。所以这一段里真正被运行时用到的只有
/// <see cref="Outputs"/>（分隔符报文按它映射业务名）；
/// <see cref="SolutionPath"/> 与 <see cref="Procedure"/> 现在只由启动自检用来
/// 检查"文件在不在"，起的是文档与排障作用。
/// </para>
/// </summary>
public class VmRecipeData
{
    /// <summary>流程文件（.sol）路径。相对路径按 exe 目录解析。自检会检查它是否可访问。</summary>
    public string SolutionPath { get; set; } = "";

    /// <summary>
    /// 方案密码（无密码留空）。
    /// 现在运行时不打开方案，所以这个值不会被用到；
    /// 留着是因为它属于"配方自述"，现场交接时要能查到。
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>要运行的流程名，如 "流程1"（运行时同样不再使用，只作自述）</summary>
    public string Procedure { get; set; } = "流程1";

    // 已删除（2026-10-27）：原来这里有个 ImageSourceModule 字段，用于"本程序调 SDK 时把帧喂进哪个模块"。
    // 相机归 VM 独占、图由 VM 自己采之后，本程序没有任何地方读它，它却仍是配方必填项 ——
    // 填了没用、不填启动报错，属于纯粹的结构遗留，已连同 Validate 里的必填校验一起移除。
    // 老配方 JSON 里残留的 "imageSourceModule" 键不影响加载：反序列化默认忽略未知成员。

    /// <summary>
    /// 输出映射表：.sol 里的模块 → 报告里的结果项。
    ///
    /// <para>
    /// <b>顺序就是语义</b>：分隔符格式（<c>OK;12.345;8</c>）没有字段名，
    /// 解析器按"第 N 段 = <c>Outputs[N-1]</c>"对号入座。
    /// 所以<b>调整数组顺序 = 改变每一段的业务含义</b>，而且代码不会报错，
    /// 只会把"外径"的值记到"孔数"名下 —— 这是最难查的一类事故。
    /// 换顺序时必须同步改 VM 侧「发送数据」的订阅顺序。
    /// </para>
    /// <para>
    /// 另有一条容易踩的规则：报文里的**图像路径段不占槽位**（见 <c>VmResultParser.ResolveImagePath</c>），
    /// 解析器会在映射时把跳过的那几段减掉。所以"配了 3 个输出、报文里有 1 段路径"
    /// 时，第 2 段仍然是 <c>Outputs[1]</c>，不会整体后移。
    /// </para>
    /// <para>
    /// 为什么要有它：模块名和输出名是工程师在平台里起的，代码不可能预先知道。
    /// 把映射放进配方，换 .sol 只改 json，不用重新编译 ——
    /// 这也是"平台 + 二次开发"里 C# 唯一必须维护的东西。
    /// </para>
    /// </summary>
    public List<VmOutputMapping> Outputs { get; set; } = new();
}

/// <summary>
/// 一条 VM 输出映射：一个模块的一项输出 → 报告里的一项结果。
///
/// <para>
/// <b>它在链路的哪一环</b>：只被 <c>VmResultParser.ParseDelimited</c> 读（分隔符格式没字段名），
/// 按数组下标对应报文的第几段。JSON 格式自带 label，完全不看这张表。
/// </para>
/// <para>
/// <b>现状</b>：运行时真正被读取的只有 <see cref="Label"/>、<see cref="Role"/>、
/// <see cref="Metric"/>、<see cref="Unit"/> 四个字段。
/// <see cref="Module"/> 只剩"给人看 / 将来扫描核对"的作用。
/// </para>
/// <para>
/// <b>2026-10-27 删掉了 <c>Io</c> 与 <c>IoKind</c></b>：它们是 SDK 时代"按输出名取浮点/圆半径"
/// 的遗留（那段代码已随 SDK 一起删除），两个字段零消费者却在每份配方里占位，只会误导调试。
/// </para>
/// </summary>
public class VmOutputMapping
{
    /// <summary>
    /// .sol 里的模块名（如 "圆查找1"）。
    /// 运行时不再按名字去模块里取值（那是 SDK 路径），只在自检/扫描核对与日志里用。
    /// </summary>
    public string Module { get; set; } = "";

    /// <summary>
    /// 报告里的业务名，也是 <c>Results</c> 的键（如 "外径"）。
    /// <b>它同时决定报文第 N 段落进哪一项</b> —— 改它等于改历史记录里那一列的名字，
    /// 而且同名会被 <see cref="RecipeConfig.Validate"/> 拦下（否则字典互相覆盖）。
    /// </summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// 结果角色，可选：Locate / Measure / Defect。
    /// 只影响历史记录的旧列投影（见 <see cref="ResultKeys"/> 与
    /// <see cref="InspectionReport.AggregateByRole"/>）。
    /// 留空则这一项只进完整快照（ToolResultsJson，只有 IsOk / Summary / Metrics），
    /// 三组旧列都算"未执行"→ NG。填错不会报错，只会让那一列一直是 NG ——
    /// 所以换型后要看一眼历史记录页，别只看运行页的总判定。
    /// </summary>
    public string Role { get; set; } = "";

    // 已删除（2026-10-27）：原来这里有两个字段 Io（输出名，如 "Radius"）和 IoKind（Float/Circle）。
    // 它们是 SDK 时代"按输出名去模块里取浮点或圆半径"的用法，那段代码随 SDK 一起删掉了。
    // 现在报文直接给数值、按数组下标对位，两个字段零消费者 —— 留着只会让人以为"填了有用"。
    // 老配方 JSON 里残留的 "io"/"ioKind" 键不影响加载：反序列化默认忽略未知成员。

    /// <summary>
    /// 写进 <c>Metrics</c> 的键名，留空用 <see cref="Label"/>。
    /// 注意它可能被存储层当列名用（如 <c>DesignedDiameter_mm</c>），
    /// 所以不是随便起的展示名 —— 起错了旧列就是 0。
    /// </summary>
    public string Metric { get; set; } = "";

    /// <summary>物理单位，仅用于摘要显示（如 "mm"）。会直接拼在数值后面，别写成长句。</summary>
    public string Unit { get; set; } = "";
}

