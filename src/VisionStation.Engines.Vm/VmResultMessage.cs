namespace VisionStation.Engines.Vm;

/// <summary>
/// VisionMaster 推给上位机的一条检测结果（一行 JSON）。
///
/// 为什么是这个形状：VM 侧用流程里的「发送数据」模块拼一行字符串发出来，
/// 绑定模块的输出端子即可，**不需要在 VM 里写脚本**。
/// 字段全部可空 —— 少给一个字段就少一条信息，不该让整帧解析失败。
///
/// VM 侧「发送数据」的格式串示例（方括号里是绑定的变量）：
/// <code>
/// {"verdict":"[判定]","imagePath":"[图像路径]","items":[{"label":"外径","value":[圆查找1.Radius],"unit":"px","ok":[圆查找1.IsRunOK]}]}
/// </code>
/// </summary>
public sealed class VmResultMessage
{
    /// <summary>VM 的总判定：OK / NG（大小写不敏感，也接受 Pass / Fail）。留空则由各项推导。</summary>
    public string? Verdict { get; set; }

    /// <summary>
    /// VM 侧存下的图（可选）。给了就能做溯源、界面显示、标注叠加；
    /// 没给也能入库 —— 结果本来就该和"有没有图"解耦。
    /// </summary>
    public string? ImagePath { get; set; }

    /// <summary>触发来源（PLC / Timer / Manual），留空记作 VM</summary>
    public string? TriggerSource { get; set; }

    /// <summary>VM 侧的检测时刻；留空用收到的时间</summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>产品序列号（追溯用）。VM 里从扫码模块取，没有就留空。</summary>
    public string? Sn { get; set; }

    /// <summary>各检测项。为空时只看 <see cref="Verdict"/>。</summary>
    public List<VmResultItem>? Items { get; set; }
}

/// <summary>一个检测项。</summary>
public sealed class VmResultItem
{
    /// <summary>业务名（报告里的键，也是界面显示的名字）</summary>
    public string? Label { get; set; }

    /// <summary>测量值（没有数值的项就留空，例如"有无"类检测）</summary>
    public double? Value { get; set; }

    /// <summary>单位，仅显示用</summary>
    public string? Unit { get; set; }

    /// <summary>这一项是否合格。留空按 NG —— 没有判定的结果不能算合格。</summary>
    public bool? Ok { get; set; }

    /// <summary>Locate / Measure / Defect，只影响历史记录的角色投影，可空</summary>
    public string? Role { get; set; }

    /// <summary>写进 Metrics 的键名，留空用 Label</summary>
    public string? Metric { get; set; }
}
