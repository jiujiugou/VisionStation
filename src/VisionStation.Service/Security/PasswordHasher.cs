using System.Security.Cryptography;
using System.Text;

namespace VisionStation.Service.Security;

/// <summary>算好的一组密码材料。三个字段一起入库，缺一不可。</summary>
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
/// </summary>
public static class PasswordHasher
{
    /// <summary>当前标准轮数</summary>
    public const int CurrentIterations = 120_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>
    /// 生成一组新的密码材料。
    /// </summary>
    /// <param name="password">明文密码</param>
    /// <param name="iterations">轮数；测试里可以调小，生产一律用默认</param>
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

    private static byte[] Derive(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashBytes);
}
