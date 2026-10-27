namespace VisionStation.Engines.Vm;

/// <summary>
/// VisionMaster 推给上位机的一条检测结果（一行 JSON）。
///
/// <para>
/// <b>定位</b>：纯 DTO，唯一的消费者是 <c>VmResultParser.ToReport</c>（由它反序列化后转成
/// <c>InspectionReport</c>）。本类没有任何逻辑、没有校验、不认识数据库。
/// 分隔符格式<b>不经过</b>这个类 —— 那条路直接按 <c>;</c> 切片。
/// </para>
///
/// <para>
/// <b>为什么字段全可空</b>：少给一个字段就少一条信息，不该让整帧解析失败。
/// 空值如何解释由解析器决定（例如 <c>Verdict</c> 为空时看 items，<c>Ok</c> 为空按 NG），
/// 而不是在这里塞默认值 —— DTO 一旦有"默认语义"，同一个空值在报文和代码里就会有两个意思。
/// </para>
///
/// <para>
/// <b>反序列化选项</b>（见 <c>VmResultParser.JsonOptions</c>）：属性名大小写不敏感、
/// 允许注释与尾逗号。<b>未知字段会被静默忽略</b> —— 这是好事：
/// VM 侧以后多加一个输出，不会让整条报文解析失败。
/// </para>
///
/// <para>
/// 为什么是这个形状：VM 侧用流程里的「发送数据」模块拼一行字符串发出来，
/// 绑定模块的输出端子即可，**不需要在 VM 里写脚本**。
/// </para>
///
/// <para>
/// VM 侧「发送数据」的格式串示例（方括号里是绑定的变量）：
/// <code>
/// {"verdict":"[判定]","imagePath":"[图像路径]","items":[{"label":"外径","value":[圆查找1.Radius],"unit":"px","ok":[圆查找1.IsRunOK]}]}
/// </code>
/// </para>
///
/// <para>
/// <b>已知缺口</b>：<see cref="Sn"/> 能收，但 <c>ToReport</c> 不往下传，
/// <c>InspectionReport</c> / <c>InspectionRecord</c> 都没有序列号字段 ——
/// 要接 MES 追溯，得先加字段 + 迁移库，不是"配一下就有"。
/// </para>
/// </summary>
public sealed class VmResultMessage
{
    /// <summary>
    /// VM 的总判定：OK / NG（大小写不敏感，也接受 Pass / Fail / 1 / 0 / 2）。
    /// 留空则由各项推导（各项全 OK 才 OK）；既没有 items 也没有本字段 = 这一件没有任何结论。
    /// </summary>
    public string? Verdict { get; set; }

    /// <summary>
    /// VM 侧存下的图（可选）。给了就能做溯源、界面显示、标注叠加；
    /// 没给也能入库 —— 结果本来就该和"有没有图"解耦。
    ///
    /// 注意它<b>只是报文里的字符串</b>：解析器不会去验证文件是否存在
    /// （那会拖慢收包线程），路径对不对由工作流在读图时发现并记警告。
    /// </summary>
    public string? ImagePath { get; set; }

    /// <summary>触发来源（PLC / Timer / Manual），留空记作 VM。留空时由解析器的 fallback 参数兜底成 "VM"。</summary>
    public string? TriggerSource { get; set; }

    /// <summary>
    /// VM 侧的检测时刻；留空用收到的时间。
    /// 解析时会转成本地时间（<c>LocalDateTime</c>）—— 全库统一存本地时间，不做时区换算。
    /// </summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>产品序列号（追溯用）。VM 里从扫码模块取，没有就留空。**当前不会被写入报告**，见类注释。</summary>
    public string? Sn { get; set; }

    /// <summary>各检测项。为空时只看 <see cref="Verdict"/>。</summary>
    public List<VmResultItem>? Items { get; set; }
}

/// <summary>
/// 一个检测项。
///
/// <para>
/// 它是报文里的最小单位，一个 items 元素对应报告里的一条 <c>ToolResult</c>。
/// 字段全可空的原因同上：VM 侧少绑一个端子，不该让整帧报废 ——
/// 但缺 <c>ok</c> 的后果是这一项按 NG 处理（见 <c>VmResultParser.ToResult</c>），
/// 因为"没有判定的结果"不能算合格。
/// </para>
/// </summary>
public sealed class VmResultItem
{
    /// <summary>
    /// 业务名（报告里的键，也是界面显示的名字）。
    /// 留空时解析器补成"未命名项" —— 不补的话多项无名会互相覆盖，最后只剩一条。
    /// </summary>
    public string? Label { get; set; }

    /// <summary>测量值（没有数值的项就留空，例如"有无"类检测）。留空时摘要只写 OK/NG。</summary>
    public double? Value { get; set; }

    /// <summary>单位，仅显示用（不会参与换算，也不会被归一）</summary>
    public string? Unit { get; set; }

    /// <summary>这一项是否合格。留空按 NG —— 没有判定的结果不能算合格。</summary>
    public bool? Ok { get; set; }

    /// <summary>
    /// Locate / Measure / Defect，只影响历史记录的角色投影，可空。
    /// 填写错误不会报错，只会让对应的旧列一直是"未执行"（NG）——
    /// 所以换型后要看一眼历史记录页，而不是只看运行页的总判定。
    /// </summary>
    public string? Role { get; set; }

    /// <summary>写进 Metrics 的键名，留空用 Label（它可能是存储层的列名，不是随便起的展示名）</summary>
    public string? Metric { get; set; }
}
