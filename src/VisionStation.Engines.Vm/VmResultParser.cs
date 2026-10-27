using System.Globalization;
using System.Text.Json;
using VisionStation.Core.Models;

namespace VisionStation.Engines.Vm;

/// <summary>
/// VM 发来的一行结果 → <see cref="InspectionReport"/>。
///
/// **支持两种格式**（按首字符自动识别）：
/// <list type="number">
///   <item>JSON：<c>{"verdict":"OK","items":[...]}</c> —— 信息最全，首选。</item>
///   <item>分隔符：<c>OK;12.345;8</c> —— VM 4.2「发送数据」模块最省事的产物（每行订阅一个输出 + 分隔符 + 结束符）。
///   没有字段名，值按位置对应配方 <c>vm.outputs</c> 的顺序。
///   这种格式也能带图：把「输出图像」模块的路径输出订阅成一段（<c>OK;1;D:\VMOut\a.bmp</c>），
///   解析器按"有路径分隔符 + 图片扩展名"认出它，塞进 <c>ImagePath</c>，且**不占配方输出的位置**。</item>
/// </list>
///
/// 为什么必须支持第二种：VM 4.2 的「格式化」模块保存含 <c>{ }</c> 的文本会报「格式化验证失败」，
/// 也就是说走这个版本做 JSON 很别扭。与其逼用户去写全局脚本，不如让接收端两种都吃。
///
/// 判定语义（与产线一致，一条硬规则）：
///   **判定权归 VisionMaster。** 每个检测项的 OK/NG 取 VM 给的 ok 字段；
///   上位机不重新判一遍公差，也不替 VM 修正结论 —— 否则就会出现"配方和流程两份公差、
///   改一份忘另一份"的经典事故。
///
/// 本类只做三件事：解析、翻译、把"说不清"的情况说清楚（解析失败、字段缺失、判定否决）。
/// 纯函数、无 IO、可单测。
/// </summary>
public static class VmResultParser
{
    /// <summary>解析用的序列化选项。缓存下来，避免每帧新建（解析器有反射元数据开销）。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>没有明确 label 时用的结果键名。</summary>
    public const string DefaultVerdictLabel = "VM判定";

    /// <summary>
    /// 解析一行结果。**任何异常都变成可解释的 NG 报告，不抛。**
    /// </summary>
    /// <param name="line">VM 发来的一行结果（JSON 或分隔符格式，见类注释）</param>
    /// <param name="engine">报告里记的引擎名</param>
    /// <param name="triggerSourceFallback">消息里没写触发来源时用的值</param>
    /// <param name="outputs">
    /// 配方的输出映射表。**只有分隔符格式需要它**：那种格式里没有字段名，
    /// 只能靠"第几个值 = 配方里第几条映射"来对号入座。JSON 格式自带 label，不用它。
    /// </param>
    public static InspectionReport Parse(string? line, string engine = "VisionMaster",
        string triggerSourceFallback = "VM", IReadOnlyList<VmOutputMapping>? outputs = null)
    {
        if (string.IsNullOrWhiteSpace(line))
            return InspectionReport.Failure(engine, "VM 发来的结果是空行", triggerSourceFallback);

        var trimmed = line.Trim();

        // 两种格式并存的原因见类注释：JSON 是首选，但 VM 4.2 的「格式化」模块
        // 对含 { } 的文本校验不通过，现场更现实的做法是「发送数据」按分隔符拼值。
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

        if (message is null)
            return InspectionReport.Failure(engine, "VM 结果是 null", triggerSourceFallback);

        return ToReport(message, engine, triggerSourceFallback);
    }

    /// <summary>
    /// 解析分隔符格式：<c>判定;值1;值2;…</c>
    ///
    /// 这是 VM「发送数据」模块最省事的产物：模块里每行订阅一个输出、打开「分隔符」和「结束符」，
    /// 发出来就是一串用分隔符连起来的值。代价是**没有字段名**，所以值只能按位置对应配方的
    /// <c>vm.outputs</c> 顺序 —— 那条映射本来就是"这个模块的这个输出叫什么"，正好用得上。
    ///
    /// 各项的 OK/NG 沿用总判定：这种格式里 VM 只给了一个总判定，
    /// 上位机不能自己编一个"每项都 OK"出去（那就是替 VM 做判定）。
    /// </summary>
    private static InspectionReport ParseDelimited(string line, IReadOnlyList<VmOutputMapping>? outputs,
        string engine, string triggerSourceFallback)
    {
        var parts = line.Split(';', StringSplitOptions.None);
        var verdict = ParseVerdict(parts[0]);

        var itemsText = parts.Length > 1 ? parts[1..] : Array.Empty<string>();

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
            if (text.Length == 0) continue;

            // 图像那几段不占配方位置：被吃掉的段数要从下标里减掉，否则映射整体错位
            var slot = i;
            for (var j = 0; j < i; j++)
            {
                if (consumed[j]) slot--;
            }

            var mapping = outputs is not null && slot < outputs.Count ? outputs[slot] : null;
            var label = string.IsNullOrWhiteSpace(mapping?.Label) ? $"值{slot + 1}" : mapping!.Label;

            var result = new ToolResult
            {
                Role = mapping?.Role ?? "",
                // 分隔符格式里没有逐项判定，统一跟随总判定（不替 VM 造逐项结论）
                IsOk = verdict.Value,
            };

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

    private static string Preview(string text) => text.Length <= 120 ? text : text[..120] + "…";

    /// <summary>VM「输出图像」模块存出来的图，实际就这几种扩展名。</summary>
    private static readonly string[] ImageExtensions =
        [".bmp", ".jpg", ".jpeg", ".png", ".tif", ".tiff"];

    /// <summary>
    /// 从各段里认出图像路径，并把用掉的段标记到 <paramref name="consumed"/>。
    ///
    /// 两种形状（VM 把「路径」和「名称」拆成两个输出，所以两种都会遇到）：
    /// ① 完整路径一段：<c>D:\VMOut\OK\a.bmp</c>
    /// ② 目录一段 + 文件名一段：<c>D:\VMOut\OK\20261026</c> 与 <c>render_46.bmp</c>
    ///
    /// 只认第一处。**只看形状不碰磁盘** —— 同一行结果在离线回放和现场必须解析成同一种东西，
    /// "文件在不在"是工作流该报的错（它会区分路径不存在、解码失败、没有路径）。
    /// </summary>
    private static string? ResolveImagePath(string[] parts, bool[] consumed)
    {
        for (var i = 0; i < parts.Length; i++)
        {
            var text = parts[i].Trim().Trim('"');
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
                if (!HasPathSeparator(name) && HasImageExtension(name))
                {
                    consumed[i] = true;
                    consumed[i + 1] = true;
                    return text.TrimEnd('\\', '/') + "\\" + name;
                }
            }
        }

        return null;
    }

    private static bool HasPathSeparator(string text) => text.IndexOf('\\') >= 0 || text.IndexOf('/') >= 0;

    private static bool HasImageExtension(string text)
    {
        var ext = Path.GetExtension(text);
        return ext.Length > 0 && ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>把已反序列化的消息翻成报告（单测直接调它）。</summary>
    public static InspectionReport ToReport(VmResultMessage message, string engine = "VisionMaster",
        string triggerSourceFallback = "VM")
    {
        ArgumentNullException.ThrowIfNull(message);

        var trigger = string.IsNullOrWhiteSpace(message.TriggerSource)
            ? triggerSourceFallback
            : message.TriggerSource;

        var imagePath = message.ImagePath ?? "";

        var report = new InspectionReport
        {
            Timestamp = message.Timestamp?.LocalDateTime ?? DateTime.Now,
            TriggerSource = trigger,
            ImagePath = imagePath,
            Engine = engine,
        };

        // 归一成非空集合：后面要用 Count 判断，也免得 foreach 上挂空值告警
        var items = message.Items ?? new List<VmResultItem>();
        var hasItems = items.Count > 0;

        if (hasItems)
        {
            foreach (var item in items)
            {
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
                    trigger, imagePath);
            }

            report.SetToolResult(DefaultVerdictLabel, new ToolResult
            {
                IsOk = vmVerdict.Value,
                Summary = vmVerdict.Value ? "VM 判定: OK（未提供检测项明细）" : "VM 判定: NG（未提供检测项明细）",
            });

            return report;
        }

        // 明细都在，但 VM 的总判定 NG 而各项都是 OK —— 说明有条否决逻辑没体现在项上。
        // 判定权归 VM，所以以总判定为准，并显式写出来，不让它静默通过。
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
            var text = value.ToString("0.###", CultureInfo.InvariantCulture);
            result.Summary = string.IsNullOrWhiteSpace(item.Unit)
                ? $"{label}: {text}"
                : $"{label}: {text}{item.Unit}";

            if (!ok) result.Summary += "（VM 判 NG）";

            var key = string.IsNullOrWhiteSpace(item.Metric) ? label : item.Metric;
            result.Metrics[key] = value;
        }
        else
        {
            result.Summary = $"{label}: {(ok ? "OK" : "NG")}";
        }

        return result;
    }

    /// <summary>接受 OK/NG/Pass/Fail/1/0，其它值当"没给"。</summary>
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
