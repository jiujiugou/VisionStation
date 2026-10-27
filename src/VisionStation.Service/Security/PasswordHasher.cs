using System.Security.Cryptography;
using System.Text;

namespace VisionStation.Service.Security;

/// <summary>
/// 算好的一组密码材料。三个字段一起入库，缺一不可。
///
/// 为什么不是一个拼好的字符串：轮数必须能被单独读出来（升级判据），
/// 盐也必须能单独取（重算时要用同一份盐）。拼成一个 "迭代$盐$哈希" 的串，
/// 解析代码就成了新的出错点。
/// </summary>
public sealed record PasswordHashResult(string Hash, string Salt, int Iterations);

/// <summary>
/// 密码哈希 —— PBKDF2-SHA256。
///
/// 为什么不用 <c>SHA256(password)</c>：单轮哈希对 GPU 来说等于明文，
/// 现代显卡一秒能算几十亿次。PBKDF2 的价值就在于"慢"——12 万轮让单次验证
/// 花掉约 50ms，攻击者拿到库也没法批量撞。
///
/// 为什么不用 BCrypt/Argon2：.NET 内置 <c>Rfc2898DeriveBytes.Pbkdf2</c> 就能做，
/// 不引第三方包。工业上位机的依赖越少越好（每多一个包，现场部署就多一个可能缺文件的地方）。
///
/// 12 万轮是 OWASP 对 PBKDF2-SHA256 给出的下限级取值。
/// 轮数入库（不是常量散落在代码里），所以将来提到 20 万轮时老用户能自动升级。
///
/// <para>
/// <b>定位</b>：认证链路最底下的一层。调用方只有 <c>UserService</c>：
/// 建出厂账号、登录验证、改密、以及"轮数低于标准就地重算"都要经过本类。
/// 本类不认识账号、不碰数据库 —— 只做"明文 + 盐 + 轮数 ⇄ 哈希"这一件事。
/// </para>
///
/// <para>
/// <b>归属</b>：静态类、无状态，任何线程都能并发调（每次调用自带随机盐，互不影响）。
/// 没有需要释放的资源，也不参与 DI。
/// </para>
///
/// <para>
/// <b>失败行为</b>：两侧刻意不同 ——
/// <see cref="Create"/> 在参数非法时<b>抛</b>（编程错误要当场暴露：轮数填 0 等于没加密）；
/// <see cref="Verify"/> 在输入畸形时<b>返回 false</b>（它跑在登录路径上，
/// 库里一行坏记录不该让整次登录变成崩溃，也不该把异常细节漏给调用方）。
/// </para>
/// </summary>
public static class PasswordHasher
{
    /// <summary>当前标准轮数。改这个值即可完成全局升级：老库在下次登录成功时自动重算（见 NeedsUpgrade）。</summary>
    public const int CurrentIterations = 120_000;

    /// <summary>盐长度（字节）。16 字节 = 128 位随机数，一人一份，防彩虹表与"同密码同哈希"。</summary>
    private const int SaltBytes = 16;

    /// <summary>哈希长度（字节）。32 字节 = SHA256 满长输出；截短没有必要，也不省空间。</summary>
    private const int HashBytes = 32;

    /// <summary>
    /// 生成一组新的密码材料。每次都取新随机盐，所以同一密码两次调用结果不同（这是对的）。
    /// 纯 CPU 计算、量级是几十毫秒（12 万轮），别在 UI 线程上同步调 ——
    /// 登录/改密都是异步命令，等这一下不该冻界面。
    /// </summary>
    /// <param name="password">明文密码</param>
    /// <param name="iterations">轮数；测试里可以调小（否则一条用例要跑 50ms），生产一律用默认</param>
    /// <exception cref="ArgumentException">密码为空 —— 空密码没有"哈希"的意义，直接拒绝</exception>
    /// <exception cref="ArgumentOutOfRangeException">轮数 ≤ 0 —— 会退化成"哈希等于明文"</exception>
    public static PasswordHashResult Create(string password, int iterations = CurrentIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        if (iterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(iterations), iterations, "迭代轮数必须大于 0");

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, iterations);

        return new PasswordHashResult(
            Convert.ToBase64String(hash),
            Convert.ToBase64String(salt),
            iterations);
    }

    /// <summary>
    /// 验证密码。
    ///
    /// 任何一项为空 / Base64 坏了都返回 false 而不是抛异常 ——
    /// 这个方法在登录路径上，一个畸形记录不该变成崩溃。
    ///
    /// <para>
    /// 注意 <paramref name="iterations"/> 是<b>从库里读出来的</b>，不是常量：
    /// 老账号用当初的轮数验证，验证通过后由调用方（<c>UserService</c>）决定要不要重算升级。
    /// 所以本方法不判断"轮数是不是够新"，它只回答"这串明文对不对得上这组材料"。
    /// </para>
    /// <para>
    /// 返回 false 的原因刻意不细分（密码错 / 哈希坏 / 盐坏 都是 false）：
    /// 登录失败的具体原因由 <c>UserService</c> 按"账号状态"给出，
    /// 这里多吐一个枚举只会让调用方多一层无意义的分支。
    /// </para>
    /// </summary>
    public static bool Verify(string password, string hashBase64, string saltBase64, int iterations)
    {
        if (string.IsNullOrEmpty(password)
            || string.IsNullOrEmpty(hashBase64)
            || string.IsNullOrEmpty(saltBase64)
            || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(saltBase64);
            expected = Convert.FromBase64String(hashBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Derive(password, salt, iterations);

        // 定长时间比较：普通 == 会在第一个不同字节处返回，
        // 攻击者能靠计时差异一个字节一个字节地试出哈希。
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>库里的轮数低于当前标准 → 下次登录成功时应当重算并覆盖</summary>
    public static bool NeedsUpgrade(int iterations) => iterations < CurrentIterations;

    /// <summary>
    /// 实际的 PBKDF2 推导。明文统一按 UTF-8 取字节：
    /// 现场密码里出现中文/全角字符时，用默认编码（受区域设置影响）会算出与别人机器不同的哈希。
    /// </summary>
    private static byte[] Derive(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashBytes);
}
