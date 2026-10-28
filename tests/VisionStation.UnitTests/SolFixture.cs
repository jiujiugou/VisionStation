using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace VisionStation.UnitTests;

/// <summary>
/// 造"最小但结构真实"的 .sol 夹具。
///
/// <para>
/// <b>为什么不写几个随便的字节充数</b>：.sol 就是 ZIP 容器（名册在
/// <c>SolutionFile/VmServer.xml</c> 的明文里）。F-68 把契约校验接进自检第 4 项之后，
/// "文件存在"已经不足以让一项自检变绿 —— 自检要能解析出名册才谈得上核对。
/// 夹具若还是 4 个字节，测的就不是"真文件被误判"，而是"假文件恰好也被放行"。
/// </para>
///
/// <para>
/// <b>为什么不用仓库里的真方案</b>：真方案 7.9MB 且随每次 VM 导出变化，
/// 而这里要钉的是"结构规则"（名册能不能读、模块名对不对得上），不是某次导出的快照。
/// 需要"真名册长什么样"的证据另在 <c>SolutionManifestTests</c> 与手工验过的
/// <c>Config/solutions/4.0_V4.2.0.sol</c> 上。
/// </para>
/// </summary>
internal static class SolFixture
{
    /// <summary>
    /// 在 <paramref name="path"/> 写一份最小方案：ZIP + <c>SolutionFile/VmServer.xml</c>，
    /// 里面一个流程 + 若干模块（<paramref name="modules"/> 的 Name 是类型名，DisplayName 是界面名，
    /// 与真导出一致 —— 配方里的 <c>outputs[].module</c> 写的是 DisplayName）。
    /// </summary>
    public static string Write(string path, params (string Name, string DisplayName)[] modules)
    {
        var xml = new StringBuilder();
        xml.Append("<Solution SolutionName=\"测试方案\">");
        xml.Append("<Procedure DisplayName=\"流程1\" />");
        foreach (var (name, display) in modules)
            xml.Append(CultureInfo.InvariantCulture,
                $"<Module Name=\"{name}\" DisplayName=\"{display}\" Index=\"0\" />");
        xml.Append("</Solution>");

        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using var entry = zip.CreateEntry("SolutionFile/VmServer.xml").Open();
        var bytes = Encoding.UTF8.GetBytes(xml.ToString());
        entry.Write(bytes, 0, bytes.Length);

        return path;
    }

    /// <summary>
    /// 一份"能过契约"的方案：名册里只有 <c>圆查找1</c>。
    /// 自检那几条用例的配方只声明了这一个模块，用它可以走到"运行时缺失"等后继判据。
    /// </summary>
    public static string WriteCircleFind(string path) => Write(path, ("CircleFind", "圆查找1"));
}
