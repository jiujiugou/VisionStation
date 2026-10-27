using System.Globalization;
using System.Text.Json;
using VisionStation.Core.Models;

namespace VisionStation.Engines.Vm;

/// <summary>
/// VM 发来的一行结果 → <see cref="InspectionReport"/>。整个项目里唯一翻译报文的代码。
///
/// <para>
/// <b>定位</b>：链路是 VM「发送数据」→ TCP :9100（默认只绑 127.0.0.1，见 appsettings 的
/// <c>VmLink.BindAddress</c>）→ <c>VmResultListener</c> 按行切分 →
/// <c>VmInspectionDriver.OnLine</c> → <b>本类</b> → <c>InspectionWorkflow.AcceptExternalAsync</c>
/// （读图 / 入库 / 回 PLC）。本类只做翻译，不碰图、不碰库、不碰界面。
/// </para>
///
/// <para>
/// <b>归属与线程</b>：静态类，没有实例状态；仅有的两个 <c>static readonly</c> 字段
/// （序列化选项、图片扩展名白名单）构造后不再改，可安全共享 —— 所以它没有"活多久"这个概念。
/// <b>每帧在收包线程上同步调用一次</b>（TCP IO 线程，不是 UI、也不是线程池）。
/// 这个事实决定了下面两条铁律：
/// <list type="bullet">
///   <item><b>不碰磁盘</b>：判断"像不像图片路径"只看字符串形状（有没有分隔符 + 扩展名白名单），
///         绝不调 <c>File.Exists</c>。每帧一次 IO 会把这根唯一的收包线程拖慢，
///         而 VM 推帧是连续动作 —— 一旦解析变慢，驱动那边就开始丢帧（它用 <c>_busy</c> 串行化，
///         丢帧只记一条警告，界面上表现为"偶尔少一件"）。文件在不在是工作流该报的错，
///         它会区分"路径不存在 / 解码失败 / 没给路径"三种情况。</item>
///   <item><b>不抛异常</b>：异常抛出 <c>OnLine</c> 会被 catch 成一条 Error 日志，
///         这一帧就没有结论、也没有记录 —— 相当于静默丢了一件产品。
///         所以每个失败出口都返回 <see cref="InspectionReport.Failure"/>，
///         让"没判出来"以 NG 的形式进库。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>支持两种格式</b>（按首字符自动识别，见 <see cref="Parse"/>）：
/// <list type="number">
///   <item>JSON：<c>{"verdict":"OK","items":[...]}</c> —— 信息最全，首选。</item>
///   <item>分隔符：<c>OK;12.345;8</c> —— VM 4.2「发送数据」模块最省事的产物
///   （每行订阅一个输出 + 分隔符 + 结束符）。没有字段名，值按位置对应配方 <c>vm.outputs</c> 的顺序。
///   这种格式也能带图：把「输出图像」模块的路径输出订阅成一段（<c>OK;1;D:\VMOut\a.bmp</c>），
///   解析器按"有路径分隔符 + 图片扩展名"认出它，塞进 <c>ImagePath</c>，且<b>不占配方输出的位置</b>。</item>
/// </list>
/// </para>
///
/// <para>
/// 为什么必须支持第二种：VM 4.2 的「格式化」模块保存含 <c>{ }</c> 的文本会报「格式化验证失败」，
/// 也就是说走这个版本做 JSON 很别扭。与其逼用户去写全局脚本，不如让接收端两种都吃。
/// </para>
///
/// <para>
/// <b>判定语义（与产线一致，一条硬规则）</b>：
/// <b>判定权归 VisionMaster。</b> 每个检测项的 OK/NG 取 VM 给的 ok 字段；
/// 上位机不重新判一遍公差，也不替 VM 修正结论 —— 否则就会出现"配方和流程两份公差、
/// 改一份忘另一份"的经典事故。唯一"以总判定覆盖明细"的地方是
/// <see cref="ToReport"/> 里总判定 NG 而各项全 OK 的情形，那是<b>采纳 VM 的否决</b>，方向是对的。
/// </para>
///
/// <para>
/// 本类只做三件事：解析、翻译、把"说不清"的情况说清楚（解析失败、字段缺失、判定否决）。
/// 纯函数、无 IO、可单测（<c>tests/VisionStation.UnitTests/VmResultParserTests.cs</c>）。
/// </para>
/// </summary>
public static class VmResultParser
{
    /// <summary>
    /// 解析用的序列化选项。缓存下来，避免每帧新建（<c>JsonSerializerOptions</c> 首次使用
    /// 会构建并缓存反射元数据，每帧新建等于每帧重建一遍）。
    ///
    /// <para>
    /// 三个开关都是为"现场手写/手改"服务的：属性名大小写不敏感（<c>verdict</c> / <c>Verdict</c>
    /// 都认）；允许注释（VM 里粘格式串时容易带上）；允许尾逗号（手删数组最后一项最常见）。
    /// 实例在首次使用后即线程安全，所以可以 <c>static readonly</c> 共享。
    /// </para>
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 没有明确 label 时用的结果键名（"VM判定"）。
    ///
    /// 两处会用到它：JSON 只给总判定、不给 items；分隔符格式只有判定、没有任何值。
    /// 用一个人能看懂的中文键而不是 "verdict"，是因为它会直接显示在界面明细和历史快照里，
    /// 而且 <see cref="InspectionReport.AggregateByRole"/> 靠 <c>Role</c> 过滤，
    /// 这个键不参与旧列投影，叫什么都不会影响那三组列。
    /// </summary>
    public const string DefaultVerdictLabel = "VM判定";

    /// <summary>
    /// 解析一行结果。**任何异常都变成可解释的 NG 报告，不抛。**
    ///
    /// <para>
    /// <b>格式分流规则</b>：去掉首尾空白后，首字符是 <c>{</c> 或 <c>[</c> 就走 JSON，
    /// 否则一律按分隔符处理。<b>不靠"包含冒号"之类的启发式</b> ——
    /// 那种判断会在分隔符值的字符串里出现冒号时误判，而误判的后果是两个解析器都不认这行。
    /// </para>
    /// <para>
    /// <b>前置条件</b>：<paramref name="line"/> 可以是 null/空白（返回一条"空行"失败报告），
    /// 也可以是任意脏数据 —— 这里是整个链路的第一道处理，不能假设输入合法。
    /// </para>
    /// <para>
    /// <b>线程/副作用</b>：在调用线程同步执行，不碰磁盘、不写日志、不分配大对象（预览截断到 200 字符）。
    /// 返回值永不为 null。
    /// </para>
    /// </summary>
    /// <param name="line">VM 发来的一行结果（JSON 或分隔符格式，见类注释）</param>
    /// <param name="engine">报告里记的引擎名</param>
    /// <param name="triggerSourceFallback">消息里没写触发来源时用的值</param>
    /// <param name="outputs">
    /// 配方的输出映射表。**只有分隔符格式需要它**：那种格式里没有字段名，
    /// 只能靠"第几个值 = 配方里第几条映射"来对号入座。JSON 格式自带 label，不用它。
    /// 传 null 也合法（单测/离线工具会这么用），此时结果键退化成"值1/值2…"，
    /// <b>不会崩</b>，但名字对不上业务，界面上只能看出"有个数"。
    /// </param>
    public static InspectionReport Parse(string? line, string engine = "VisionMaster",
        string triggerSourceFallback = "VM", IReadOnlyList<VmOutputMapping>? outputs = null)
    {
        // 空行不是"没有结果"，而是"这次通信有问题"：必须留一条可查的痕迹，
        // 否则现场只看到"少了一件"，没有任何线索。
        if (string.IsNullOrWhiteSpace(line))
            return InspectionReport.Failure(engine, "VM 发来的结果是空行", triggerSourceFallback);

        var trimmed = line.Trim();

        // 两种格式并存的原因见类注释：JSON 是首选，但 VM 4.2 的「格式化」模块
        // 对含 { } 的文本校验不通过，现场更现实的做法是「发送数据」按分隔符拼值。
        // 只认首字符，不做"看起来像不像 JSON"的猜测 —— 猜错的代价是整行没结论。
        // 注意 '[' 也走 JSON 分支，但 VmResultMessage 是对象，数组报文会在反序列化时
        // 落进下面的 JsonException 分支（带原文返回失败），不会静默变成一条空报告。
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            return ParseDelimited(trimmed, outputs, engine, triggerSourceFallback);

        VmResultMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<VmResultMessage>(trimmed, JsonOptions);
        }
        catch (JsonException ex)
        {
            // 把原文截一段带上：VM 侧格式串配错是最常见的原因，看不到原文根本没法查
            var preview = trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
            return InspectionReport.Failure(engine,
                $"VM 结果不是合法 JSON：{ex.Message}  原文：{preview}",
                triggerSourceFallback);
        }

        // 字面量 "null" 也能反序列化成功，结果是 null —— 不单独接住的话下一句就 NRE
        if (message is null)
            return InspectionReport.Failure(engine, "VM 结果是 null", triggerSourceFallback);

        return ToReport(message, engine, triggerSourceFallback);
    }

    /// <summary>
    /// 解析分隔符格式：<c>判定;值1;值2;…</c>
    ///
    /// <para>
    /// 这是 VM「发送数据」模块最省事的产物：模块里每行订阅一个输出、打开「分隔符」和「结束符」，
    /// 发出来就是一串用分隔符连起来的值。代价是**没有字段名**，所以值只能按位置对应配方的
    /// <c>vm.outputs</c> 顺序 —— 那条映射本来就是"这个模块的这个输出叫什么"，正好用得上。
    /// </para>
    ///
    /// <para>
    /// <b>各项的 OK/NG 沿用总判定</b>：这种格式里 VM 只给了一个总判定，
    /// 上位机不能自己编一个"每项都 OK"出去（那就是替 VM 做判定）。
    /// 代价是"哪一项拖累了良率"看不出来 —— 要知道就得改用 JSON 格式带上逐项 ok。
    /// </para>
    ///
    /// <para>
    /// <b>三条边界（都被单测钉着，改前先看 <c>VmResultParserTests</c>）</b>：
    /// <list type="number">
    ///   <item>第一段必须是判定，不是就整行失败 —— 绝不"猜一个判定"。
    ///         猜错的代价不对等：把 NG 猜成 OK 会放走不良品，把 OK 猜成 NG 只是多扔一件。</item>
    ///   <item>只有判定、没有任何值（如 <c>OK</c>）是合法结论，记一条
    ///         <see cref="DefaultVerdictLabel"/>；否则报告会变成空结果集 → 恒 NG。</item>
    ///   <item>被认成图像路径的段不占配方槽位（见 <see cref="ResolveImagePath"/>），
    ///         映射时要把前面被吃掉的段数减掉。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>已知的坑（未修，记录在此）</b>：
    /// <list type="bullet">
    ///   <item><c>OK;</c>（只有判定 + 一个尾部空段）会走进"有值"分支：<c>itemCount</c> 把空段
    ///         也算一段，于是不等于 0；而循环又把空段跳过，最终结果集为空 → 按 NG 落库。
    ///         现场规避办法是 VM 侧只有判定时不要留尾部分隔符（结束符用换行，不用分号）。</item>
    ///   <item>只发目录、后面没有文件名时，那一段既拼不出文件也认不成完整路径，
    ///         会退化成一条"值N"记录（<c>docs/代码导读.md</c> 有同样的记载）；
    ///         界面侧靠"图像来源"页配的目录兜底。</item>
    /// </list>
    /// </para>
    /// </summary>
    private static InspectionReport ParseDelimited(string line, IReadOnlyList<VmOutputMapping>? outputs,
        string engine, string triggerSourceFallback)
    {
        // 只按 ';' 切，None 保留空段：空段代表"这个输出这一帧没值"，
        // 它仍然要占一个槽位（见下面 slot 的计算），不能当成不存在。
        var parts = line.Split(';', StringSplitOptions.None);
        var verdict = ParseVerdict(parts[0]);

        // 第 0 段是总判定，剩下的才是值（可能夹着图像路径段）
        var itemsText = parts.Length > 1 ? parts[1..] : Array.Empty<string>();

        // 分隔符格式没有时间戳字段（VM 只发值），Timestamp 落回对象的默认值 = 收到的时间。
        // 与"VM 判定时刻"差几毫秒到几百毫秒，可接受；但别拿它做跨设备对时。
        var report = new InspectionReport
        {
            TriggerSource = triggerSourceFallback,
            Engine = engine,
        };

        // 分隔符里也可能夹着图像信息：「发送数据」把「输出图像」模块的两项输出各订阅成一段。
        // **VM 把"路径"和"名称"拆成两个输出**：路径给到日期目录（...\20261026），
        // 文件名在「原图名称」里（render_46.bmp）。所以这里要认两种形状：
        //   ① 完整路径：D:\VMOut\OK\a.bmp
        //   ② 目录 + 下一段文件名：D:\VMOut\OK\20261026  +  render_46.bmp
        // 认出来的段**不占配方 vm.outputs 的位置** —— 那张表是给数值/状态用的，
        // 掺两段路径进去，后面所有映射会整体错位。
        var consumed = new bool[itemsText.Length];
        report.ImagePath = ResolveImagePath(itemsText, consumed) ?? "";

        // 统计"真正属于配方"的段数（空段也算：空段是被跳过的值，不是路径）。
        // 这个数是下面分流的唯一依据，不能拿 itemsText.Length 代替 —— 路径段会把它算多。
        var itemCount = 0;
        for (var i = 0; i < itemsText.Length; i++)
        {
            if (!consumed[i]) itemCount++;
        }

        // 一个值都没有：只有判定，按"只给总判定"处理（带图时也一样）
        if (itemCount == 0)
        {
            if (verdict is null)
            {
                return InspectionReport.Failure(engine,
                    $"VM 发来的分隔符结果既没有可识别的判定也没有数值：{Preview(line)}",
                    triggerSourceFallback);
            }

            report.SetToolResult(DefaultVerdictLabel, new ToolResult
            {
                IsOk = verdict.Value,
                Summary = verdict.Value ? "VM 判定: OK（未提供检测项明细）" : "VM 判定: NG（未提供检测项明细）",
            });

            return report;
        }

        // 有值但第一段不是判定：绝不能"有值就按 OK"处理。
        // 这一条挡的是 VM 侧订阅顺序配错（把某个数值订阅到了第一行）——
        // 若放过，一整批数值会被当成"某种判定"入库，良率从此不可信。
        if (verdict is null)
        {
            return InspectionReport.Failure(engine,
                $"VM 发来的分隔符结果第一段不是判定（应为 OK/NG）：{Preview(line)}",
                triggerSourceFallback);
        }

        for (var i = 0; i < itemsText.Length; i++)
        {
            if (consumed[i]) continue;

            var text = itemsText[i].Trim();
            // 空段 = 这个输出这一帧没有值（VM 侧模块未执行的常见表现）。
            // 跳过它，但**槽位仍然算它的** —— 否则后面所有值会整体前移，
            // 把"孔径"的值记到"外径"名下，而总判定还是对的，查起来极其费劲。
            if (text.Length == 0) continue;

            // 图像那几段不占配方位置：被吃掉的段数要从下标里减掉，否则映射整体错位
            var slot = i;
            for (var j = 0; j < i; j++)
            {
                if (consumed[j]) slot--;
            }

            // 映射表不够长是合法的（配方没配全）：用"值N"占位，绝不因为缺映射就丢值。
            // 丢值会让总判定与明细对不上，而总判定才是回给 PLC 的那个。
            var mapping = outputs is not null && slot < outputs.Count ? outputs[slot] : null;
            var label = string.IsNullOrWhiteSpace(mapping?.Label) ? $"值{slot + 1}" : mapping!.Label;

            var result = new ToolResult
            {
                Role = mapping?.Role ?? "",
                // 分隔符格式里没有逐项判定，统一跟随总判定（不替 VM 造逐项结论）
                IsOk = verdict.Value,
            };

            // InvariantCulture 是必须的：VM 永远用 '.' 做小数点，
            // 而在逗号做小数点的区域设置下（如 de-DE），"12.345" 会被解析成 12345。
            // 这种错不会抛，只会让所有尺寸差三个数量级，而且总判定照样"正常"。
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                result.Summary = string.IsNullOrWhiteSpace(mapping?.Unit)
                    ? $"{label}: {text}"
                    : $"{label}: {text}{mapping!.Unit}";

                var key = string.IsNullOrWhiteSpace(mapping?.Metric) ? label : mapping!.Metric;
                result.Metrics[key] = value;
            }
            else
            {
                // 非数值（"有无"类、字符串状态）照样保留，只是没有 Metric 可写
                result.Summary = $"{label}: {text}";
            }

            report.SetToolResult(label, result);
        }

        return report;
    }

    /// <summary>
    /// 失败信息里附带的原文片段。截到 120 字符有两个原因：
    /// 一是界面日志面板一行放不下；二是错误报告会进库/进日志，
    /// 一整行几 KB 的脏数据重复上千次会把日志文件撑爆。
    /// </summary>
    private static string Preview(string text) => text.Length <= 120 ? text : text[..120] + "…";

    /// <summary>
    /// VM「输出图像」模块存出来的图，实际就这几种扩展名。
    ///
    /// 这是一张<b>白名单</b>，不是"随便猜扩展名"：只有命中它才可能被当成图片路径。
    /// 反过来，如果现场改了存图格式（比如存 .webp），这里不补就会把路径当成数值项 ——
    /// 表现是界面上出现一条"值N: D:\..."，而不是报错。改存图格式时记得同步这里。
    /// </summary>
    private static readonly string[] ImageExtensions =
        [".bmp", ".jpg", ".jpeg", ".png", ".tif", ".tiff"];

    /// <summary>
    /// 从各段里认出图像路径，并把用掉的段标记到 <paramref name="consumed"/>。
    ///
    /// <para>
    /// 两种形状（VM 把「路径」和「名称」拆成两个输出，所以两种都会遇到）：
    /// ① 完整路径一段：<c>D:\VMOut\OK\a.bmp</c>
    /// ② 目录一段 + 文件名一段：<c>D:\VMOut\OK\20261026</c> 与 <c>render_46.bmp</c>
    /// </para>
    ///
    /// <para>
    /// <b>只认第一处</b>（<c>return</c> 掉）：一行只有一个 ImagePath 字段，认多了也没地方放，
    /// 反而会把后面的真实数值段吃掉。
    /// </para>
    ///
    /// <para>
    /// <b>只看形状，绝不碰磁盘</b>：同一行结果在离线回放和现场必须解析成同一种东西；
    /// 更重要的是本方法跑在收包线程上，每帧若干次 <c>File.Exists</c> 在网络盘/杀毒软件扫描下
    /// 可以慢到几十毫秒 —— 那点时间足够让驱动判定"上一帧还没处理完"并丢掉下一帧。
    /// "文件在不在"是工作流该报的错（它会区分路径不存在、解码失败、没有路径三种情况）。
    /// </para>
    ///
    /// <para>
    /// <b>失败行为</b>：认不出就返回 null，<b>不报错</b> —— 绝大多数帧本来就不带图，
    /// 那不是异常。调用方会把 null 转成空串，入库时 ImagePath 为空，界面上显示"无图"。
    /// </para>
    /// </summary>
    private static string? ResolveImagePath(string[] parts, bool[] consumed)
    {
        for (var i = 0; i < parts.Length; i++)
        {
            // 去空格再去引号：VM 的路径输出有时会带一对双引号（复制粘贴进格式串的常见残留）
            var text = parts[i].Trim().Trim('"');
            // 没有分隔符的一定不是路径。"12.345" 里没有 '/' 也没有 '\'，
            // 所以数值永远不会被误认 —— 单测"数值不会被误认成图像路径"钉的就是这条。
            if (!HasPathSeparator(text)) continue;

            if (HasImageExtension(text))
            {
                consumed[i] = true;
                return text;
            }

            // 目录 + 下一段文件名：只有"下一段是不带路径的图片文件名"才算，避免把普通值拼进来
            if (i + 1 < parts.Length)
            {
                var name = parts[i + 1].Trim().Trim('"');
                // 下一段自己带分隔符 = 那是另一条完整路径（或 URL），拼起来只会得到垃圾；
                // 下一段没有图片扩展名 = 那是一个值，不是文件名。
                if (!HasPathSeparator(name) && HasImageExtension(name))
                {
                    // 两段都要标记消费：只标目录会让文件名变成一条"值N"结果
                    consumed[i] = true;
                    consumed[i + 1] = true;
                    // 固定用 '\\' 拼接：现场是 Windows。Linux 风格的分隔符只在单测里出现，
                    // 而且那些用例给的都是"完整路径一段"，不会走到这条拼接分支。
                    return text.TrimEnd('\\', '/') + "\\" + name;
                }
            }
        }

        return null;
    }

    /// <summary>判断一段文本里有没有路径分隔符。两种都认：VM 侧可能给 Windows 或 POSIX 风格。</summary>
    private static bool HasPathSeparator(string text) => text.IndexOf('\\') >= 0 || text.IndexOf('/') >= 0;

    /// <summary>
    /// 扩展名是否在白名单里。用 <see cref="Path.GetExtension"/> 而不是字符串 <c>EndsWith</c>：
    /// 前者对"没有扩展名"的目录名返回空串，正好把 <c>D:\VMOut\20261026</c> 这种日期目录排除掉。
    /// 大小写不敏感，因为 VM 存图可能给 <c>.BMP</c>。
    /// </summary>
    private static bool HasImageExtension(string text)
    {
        var ext = Path.GetExtension(text);
        return ext.Length > 0 && ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把已反序列化的消息翻成报告（单测直接调它，不经过 JSON 解析）。
    ///
    /// <para>
    /// <b>判定优先级的完整规则</b>（这是全类最容易看错的地方）：
    /// <list type="number">
    ///   <item>有 items：<b>逐项</b>用各自的 <c>ok</c> 定（缺失按 NG）；
    ///         此时总判定只在"NG 而各项全 OK"时起作用（补一条否决项）。</item>
    ///   <item>无 items、有总判定：记一条 <see cref="DefaultVerdictLabel"/>，结论就是总判定。</item>
    ///   <item>两者都没有：返回 <see cref="InspectionReport.Failure"/>，不能返回"空且 OK"。</item>
    /// </list>
    /// 也就是说：单项的结论只由它自己的 <c>ok</c> 决定，总判定改不了某一项；
    /// 但总判定可以把"明细全 OK"的整帧否掉 —— 判定权在 VM，上位机没有资格驳回它的否决。
    /// </para>
    ///
    /// <para>
    /// <b>失败行为</b>：唯一会抛的是参数为 null（<see cref="ArgumentNullException"/>）——
    /// 那是调用方的 bug（<see cref="Parse"/> 里已经先挡过一次），不是"报文有问题"。
    /// 报文层面的问题一律走返回 Failure。
    /// </para>
    ///
    /// <para>
    /// <b>已知边界</b>：items 里 label 重复时后一条覆盖前一条（<c>SetToolResult</c> 是索引赋值），
    /// 本方法不去重也不报错 —— 分隔符格式靠配方校验拦重名，JSON 格式靠 VM 侧自己保证。
    /// 另外 <c>message.Sn</c> 会原样（裁掉首尾空白后）写进报告的 <c>Sn</c>，
    /// 但**只有 JSON 报文带得到它**：分隔符格式没有字段名，没有给 SN 留槽位。
    /// </para>
    /// </summary>
    public static InspectionReport ToReport(VmResultMessage message, string engine = "VisionMaster",
        string triggerSourceFallback = "VM")
    {
        ArgumentNullException.ThrowIfNull(message);

        // 报文没写来源就记 fallback（"VM"）—— 不能留空，
        // 否则事后无法区分"VM 推的"和"某个旧工具灌进来的历史数据"。
        var trigger = string.IsNullOrWhiteSpace(message.TriggerSource)
            ? triggerSourceFallback
            : message.TriggerSource;

        // null 归一成空串：下游会拿它去拼路径/写库，null 在那里是另一套分支
        var imagePath = message.ImagePath ?? "";

        // SN 同理归一成空串，并且 **Trim**：VM 侧扫码模块的字符串常带尾部空格或换行
        // （格式串里粘过来的），不裁掉的话按条码查历史会查不到 —— 界面上看到的和存下来的
        // 差一个看不见的空格，是最难自查的一类问题。
        var sn = message.Sn?.Trim() ?? "";

        var report = new InspectionReport
        {
            // 有 VM 的时间就用 VM 的（它才是判定时刻），没有才退回本机收到的时间。
            // LocalDateTime 而不是原样 DateTimeOffset：全库统一存本地时间，不做时区换算。
            Timestamp = message.Timestamp?.LocalDateTime ?? DateTime.Now,
            TriggerSource = trigger,
            ImagePath = imagePath,
            Sn = sn,
            Engine = engine,
        };

        // 归一成非空集合：后面要用 Count 判断，也免得 foreach 上挂空值告警
        var items = message.Items ?? new List<VmResultItem>();
        var hasItems = items.Count > 0;

        if (hasItems)
        {
            foreach (var item in items)
            {
                // 没 label 也要留一条：丢掉它就等于丢掉一个检测项，
                // 而总判定可能正是被这一项否掉的 —— 那会变成"NG 但查不出为什么"。
                var label = string.IsNullOrWhiteSpace(item.Label) ? "未命名项" : item.Label;
                report.SetToolResult(label, ToResult(item, label));
            }
        }

        var vmVerdict = ParseVerdict(message.Verdict);

        if (!hasItems)
        {
            // 只给总判定、没给明细：照样是一条合法结论（"VM 判定这件 OK"），
            // 但必须留痕，否则事后查不出它到底检了什么。
            if (vmVerdict is null)
            {
                return InspectionReport.Failure(engine,
                    "VM 结果既没有检测项（items）也没有总判定（verdict），这一件没有任何结论",
                    trigger, imagePath, sn);
            }

            report.SetToolResult(DefaultVerdictLabel, new ToolResult
            {
                IsOk = vmVerdict.Value,
                Summary = vmVerdict.Value ? "VM 判定: OK（未提供检测项明细）" : "VM 判定: NG（未提供检测项明细）",
            });

            return report;
        }

        // 明细都在，但 VM 的总判定 NG 而各项都是 OK —— 说明有条否决逻辑没体现在项上
        // （比如 VM 脚本里的综合判定、或某个没下发的条件）。
        // 判定权归 VM，所以以总判定为准，并显式写出来，不让它静默通过：
        // 少写这一条，界面上会出现"总判定 NG、每一项都打着 OK"，谁看都以为程序坏了。
        if (vmVerdict == false && report.IsOk)
        {
            report.SetToolResult(DefaultVerdictLabel, new ToolResult
            {
                IsOk = false,
                Summary = "VM 总判定为 NG，但各项均为 OK（VM 侧有未下发的否决条件，按 NG 处理）",
            });
        }

        return report;
    }

    /// <summary>
    /// 一个 JSON 检测项 → <see cref="ToolResult"/>。
    ///
    /// <para>
    /// <b>不抛、不校验业务合理性</b>：数值范围、单位、公差都不在这里管 ——
    /// 判定已经由 VM 做完并通过 <c>ok</c> 传过来了，这里只做"翻译"。
    /// 在上位机再判一次公差，就是给"配方 / .sol / 上位机"三份判据，改一份忘两份。
    /// </para>
    /// </summary>
    private static ToolResult ToResult(VmResultItem item, string label)
    {
        // ok 留空按 NG：一项没有判定的结果不能算合格
        var ok = item.Ok ?? false;

        var result = new ToolResult
        {
            Role = item.Role ?? "",
            IsOk = ok,
        };

        if (item.Value is { } value)
        {
            // 固定三位小数：VM 会给 12.3450000001 这种浮点尾巴，直接 ToString 会让摘要长得没法看，
            // 也会让"同一件产品两次判定的摘要文本不同"这种无意义差异出现。
            var text = value.ToString("0.###", CultureInfo.InvariantCulture);
            result.Summary = string.IsNullOrWhiteSpace(item.Unit)
                ? $"{label}: {text}"
                : $"{label}: {text}{item.Unit}";

            // 值有了但判 NG：必须在摘要里点明，否则列表里看到"外径: 12.3"会以为合格
            if (!ok) result.Summary += "（VM 判 NG）";

            var key = string.IsNullOrWhiteSpace(item.Metric) ? label : item.Metric;
            result.Metrics[key] = value;
        }
        else
        {
            // 无数值项（有无、状态类）：摘要只能是 OK/NG。
            // 这一支最常见的误用是"VM 没绑值"—— 摘要会一直显示 NG，看着像误判，其实是报文没配全。
            result.Summary = $"{label}: {(ok ? "OK" : "NG")}";
        }

        return result;
    }

    /// <summary>
    /// 判定文本归一。接受 OK/NG/Pass/Fail/1/0，其它值当"没给"（返回 null）。
    ///
    /// <para>
    /// <b>为什么认这么多写法</b>：JSON 格式一般是人写的字符串（OK/NG）；
    /// 分隔符格式里第一段常常直接订阅 VM 的「模块状态」或「运行状态」，
    /// 那是数值，而 VM 的模块状态码是 <b>0 = 未运行 / 1 = OK / 2 = NG</b>。
    /// 所以 0 和 2 都必须算 NG —— 只认 0 的话，真 NG 的 2 会被当成"不认识的判定"整行失败，
    /// 界面上就是一条解析错误而不是不良品。
    /// </para>
    ///
    /// <para>
    /// <b>优先级</b>：先 Trim → 再 <c>ToUpperInvariant</c> → 再精确匹配整串。
    /// 不做"包含 OK 就算 OK"这种模糊匹配：那会让 "NOTOK"、"OK?" 一类脏值被当成合格，
    /// 而这是唯一一条"判错就放走不良品"的路径。
    /// </para>
    ///
    /// <para>
    /// <b>失败行为</b>：认不出返回 null（不是 false）——
    /// 调用方据此区分"VM 说 NG"和"这行压根没说判定"，前者入库、后者报解析失败。
    /// </para>
    /// </summary>
    private static bool? ParseVerdict(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        switch (text.Trim().ToUpperInvariant())
        {
            case "OK":
            case "PASS":
            case "TRUE":
            case "1":      // VM 的模块状态：1 = OK
                return true;
            case "NG":
            case "FAIL":
            case "FALSE":
            case "0":      // VM 的模块状态：0 = 未运行 / 未判定
            case "2":      // VM 的模块状态：2 = NG
                return false;
            default:
                return null;
        }
    }
}
