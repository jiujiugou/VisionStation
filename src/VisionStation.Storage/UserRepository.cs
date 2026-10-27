using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VisionStation.Core.Users;
using VisionStation.Storage.Entities;

namespace VisionStation.Storage;

/// <summary>
/// 用户账号数据访问契约。
///
/// 与检测记录/审计分开：账号表是全系统唯一会被 UPDATE 的表
/// （改密码、停用、记最后登录），而另外两张是纯追加。混在一起，
/// 将来想给"只追加"的表做防篡改时会很别扭。
///
/// <para>
/// <b>刻意没有 Delete</b>：账号只停用（<see cref="SetEnabledAsync"/>）、不删除。
/// 审计里记的是账号名（不是外键），删掉一个账号等于让三个月前那些
/// "谁改的参数"变成查无此人 —— 为了让界面上的列表短一行而毁掉追溯链，不值得。
/// </para>
///
/// <para>
/// <b>失败行为不统一，这是有意的</b>：
/// <see cref="FindAsync"/> 查不到返回 <c>null</c>（"这个账号不存在"是正常业务结果）；
/// <see cref="UpdatePasswordAsync"/> 找不到账号**抛**（要改密的账号不存在，
/// 说明界面状态和库不一致，必须喊出来）；<see cref="TouchLoginAsync"/> /
/// <see cref="SetEnabledAsync"/> 找不到返回 <c>false</c>（登录已经成功了，
/// 记一笔时间失败不该把登录推翻）。
/// </para>
///
/// <para>
/// <b>谁在调</b>：只有 <c>UserService</c>（认证、改密、停用、出厂管理员种子）。
/// 界面拿的是 <c>UserAccount</c>，不认识本接口 —— 密码材料不往 UI 层漏。
/// </para>
/// </summary>
public interface IUserRepository
{
    Task EnsureCreatedAsync();

    /// <summary>按登录名查（大小写不敏感）。找不到返回 null。</summary>
    Task<UserAccount?> FindAsync(string userName);

    /// <summary>全部账号（含已停用 —— 用户管理界面要能看到"这个人被停用了"）</summary>
    Task<IReadOnlyList<UserAccount>> GetAllAsync();

    /// <summary>
    /// 新增或按 UserName 覆盖。
    ///
    /// <para>
    /// 覆盖时**不动 CreatedAt**（那一列只在新增时写），也不会改 UserName ——
    /// 用户名是主键，改名等于换人，要换人就停用旧的、建新的（审计靠名字串起历史）。
    /// </para>
    /// </summary>
    Task UpsertAsync(UserAccount user);

    /// <summary>只改密码材料（登录成功后自动升级轮数也走这里）</summary>
    Task UpdatePasswordAsync(string userName, string hash, string salt, int iterations, bool mustChangePassword);

    /// <summary>登录成功时记一笔时间</summary>
    Task TouchLoginAsync(string userName, DateTime at);

    Task SetEnabledAsync(string userName, bool enabled);
}

/// <summary>
/// 用户账号数据访问实现。
///
/// 与另外两个仓储相同的惯例：每次操作一个 DbContext（登录发生在 UI 线程，
/// 用户管理可能并发，共享 context 会撞 EF 的并发禁止）。
///
/// <para>
/// <b>归属</b>：<c>AddStorage</c> 里注册为 DI 单例，活到容器释放；本类不持有任何 context，
/// 只持有 <c>new</c> 它的工厂，所以单例不会退化成"全程序共用一个 DbContext"。
/// </para>
/// </summary>
public class UserRepository : IUserRepository
{
    /// <summary>取 context 的工厂；<c>Owned</c>=false 表示是别人给的 context（测试），不要替它释放。</summary>
    private readonly Func<(InspectionDbContext Db, bool Owned)> _newContext;

    /// <summary>日志。可空：测试不传也能用。</summary>
    private readonly ILogger<UserRepository>? _logger;

    /// <summary>生产用：每次操作独立 context。dbPath 是已解析的绝对路径（<c>AppPaths.DbPath</c>）。</summary>
    public UserRepository(string dbPath, ILogger<UserRepository>? logger = null)
    {
        _newContext = () => (new InspectionDbContext(dbPath), true);
        _logger = logger;
    }

    /// <summary>测试用：复用传入的 context，**不可并发**（同一个 context 只能一个线程用）。</summary>
    public UserRepository(InspectionDbContext db, ILogger<UserRepository>? logger = null)
    {
        _newContext = () => (db, false);
        _logger = logger;
    }

    /// <summary>
    /// 统一外壳：拿 context → 干活 → 释放自己那份。
    ///
    /// 与 <c>InspectionRepository.RunAsync</c> 的差别是**这里不做异常翻译**：
    /// "数据库被锁定，本条检测结果未入库"那句是给检测结果用的，
    /// 出现在登录失败上会把人引到错误的方向。账号操作失败就让它带着原文上去。
    /// </summary>
    private async Task<T> RunAsync<T>(Func<InspectionDbContext, Task<T>> work)
    {
        var (db, owned) = _newContext();
        try
        {
            return await work(db).ConfigureAwait(false);
        }
        finally
        {
            if (owned) await db.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 建表 + 补结构。与检测仓储调的是同一个 <see cref="StorageSchema"/>，
    /// 所以谁先跑都行；第二个跑的时候什么都不缺，代价只有几次元数据查询。
    /// </summary>
    public Task EnsureCreatedAsync() => RunAsync(async db =>
    {
        var (journal, addedTables, addedColumns) =
            await StorageSchema.EnsureUpToDateAsync(db).ConfigureAwait(false);

        if (addedTables.Count > 0)
            _logger?.LogWarning("[用户] 结构升级：新建缺失表 {Tables}", string.Join(", ", addedTables));
        if (addedColumns.Count > 0)
            _logger?.LogWarning("[用户] 结构升级：补充缺失列 {Columns}", string.Join(", ", addedColumns));
        _logger?.LogInformation("[用户] 表就绪（journal={Journal}）", journal);
        return true;
    });

    /// <summary>
    /// 按登录名查。**大小写不敏感**，是登录链路上唯一一次读账号表。
    ///
    /// <para>
    /// 为什么不用主键直接查（主键就是 UserName，一次索引命中）：主键比较是二进制的，
    /// 操作员把 "Admin" 敲成 "admin" 就登不上了，而这种事故在工控机上每天都会发生。
    /// 代价是全表扫描 —— 账号表只有个位数行，可以忽略。
    /// </para>
    /// <para>
    /// <b>注意 SQLite 的 <c>lower()</c> 只折叠 ASCII</b>：中文用户名不受影响（本来没有大小写），
    /// 但带变音的拉丁字母（如 <c>Ö</c>）不会被折叠。现场用纯 ASCII 账号名时这条无感。
    /// </para>
    /// </summary>
    public Task<UserAccount?> FindAsync(string userName) => RunAsync(async db =>
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;

        // 大小写不敏感：操作员在工控机上用小键盘输名字，没人会注意大小写。
        // 表只有个位数行，lower() 用不上索引也无所谓。
        var key = userName.Trim().ToLowerInvariant();
        var row = await db.Users
            .FirstOrDefaultAsync(u => u.UserName.ToLower() == key)
            .ConfigureAwait(false);

        return row is null ? null : ToAccount(row);
    });

    /// <summary>
    /// 全部账号（含已停用），按名字排序。
    ///
    /// 不过滤 <c>IsEnabled</c>：用户管理界面要让工程师看见"这个人被停用了"，
    /// 过滤掉之后就分不清"没有这个人"和"被停用了"。
    /// 按名字排序只为界面稳定 —— 不然每次刷新顺序都可能变，翻找更费劲。
    /// </summary>
    public Task<IReadOnlyList<UserAccount>> GetAllAsync() => RunAsync(async db =>
    {
        var rows = await db.Users.OrderBy(u => u.UserName).ToListAsync().ConfigureAwait(false);
        return (IReadOnlyList<UserAccount>)rows.Select(ToAccount).ToList();
    });

    /// <summary>
    /// 新增或按 UserName 覆盖。
    ///
    /// <para>
    /// 覆盖分支逐字段赋值，而不是 <c>db.Users.Update(record)</c>：
    /// <c>Update</c> 会把整行（含导航属性状态）标成全修改，将来加列时容易把
    /// 调用方没填的字段一起写成默认值。逐字段列出来，改哪个是看得见的。
    /// </para>
    ///
    /// <para>
    /// <b>这里的匹配是大小写敏感的主键比较</b>（与 <see cref="FindAsync"/> 不一致）：
    /// 传 "admin" 而库里是 "Admin" 时，会走新增分支并撞主键，抛异常。
    /// 当前生产只有一个调用点（建出厂管理员，名字是常量），不会触发；
    /// 但将来做"新增用户"界面时必须先在入口统一大小写，否则会变成偶发的主键冲突。
    /// </para>
    /// </summary>
    public Task UpsertAsync(UserAccount user) => RunAsync(async db =>
    {
        var existing = await db.Users
            .FirstOrDefaultAsync(u => u.UserName == user.UserName)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.Users.Add(ToRecord(user));
        }
        else
        {
            existing.DisplayName = user.DisplayName;
            existing.Role = user.Role;
            existing.PasswordHash = user.PasswordHash;
            existing.PasswordSalt = user.PasswordSalt;
            existing.PasswordIterations = user.PasswordIterations;
            existing.IsEnabled = user.IsEnabled;
            existing.MustChangePassword = user.MustChangePassword;
            existing.LastLoginAt = user.LastLoginAt;
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        _logger?.LogInformation("[用户] 保存账号 {User}（角色：{Role}）",
            user.UserName, RolePermissions.Describe(user.Role));
        return true;
    });

    /// <summary>
    /// 只改密码材料（登录成功后自动升级轮数也走这里）。
    ///
    /// <para>
    /// 账号不存在就**抛**：这条路径是"已经确认过这个人存在"之后才走的
    /// （改密流程 / 登录成功后的轮数升级），此时查不到说明库在别处被动过。
    /// 静默返回会让调用方以为密码已经换了，实际下一次登录还是老密码 —— 最难解释的一类故障。
    /// </para>
    /// <para>
    /// 连 <c>MustChangePassword</c> 一起传：登录升级轮数时要把"是否仍需强制改密"原样带过来，
    /// 不传就等于登录一次替他清掉了改密要求。
    /// </para>
    /// </summary>
    public Task UpdatePasswordAsync(
        string userName, string hash, string salt, int iterations, bool mustChangePassword) => RunAsync(async db =>
    {
        var row = await db.Users.FirstOrDefaultAsync(u => u.UserName == userName).ConfigureAwait(false);
        if (row is null)
            throw new InvalidOperationException($"账号不存在：{userName}");

        row.PasswordHash = hash;
        row.PasswordSalt = salt;
        row.PasswordIterations = iterations;
        row.MustChangePassword = mustChangePassword;

        await db.SaveChangesAsync().ConfigureAwait(false);
        _logger?.LogInformation("[用户] 已更新密码材料 {User}（轮数 {Iterations}）", userName, iterations);
        return true;
    });

    /// <summary>
    /// 登录成功时记一笔时间。账号不存在返回 false，不抛 ——
    /// 登录已经成功了，记时间失败不该反过来推翻一次合法登录。
    /// </summary>
    public Task TouchLoginAsync(string userName, DateTime at) => RunAsync(async db =>
    {
        var row = await db.Users.FirstOrDefaultAsync(u => u.UserName == userName).ConfigureAwait(false);
        if (row is null) return false;

        row.LastLoginAt = at;
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    });

    /// <summary>
    /// 启用 / 停用。停用不删除（理由见 <see cref="IUserRepository"/> 的说明）。
    /// 找不到账号返回 false；成功时用 Warning 级别记一条 —— 这是一个"改变谁能登录"的动作，
    /// 混在 Information 里翻日志时容易被当普通流水跳过。
    /// </summary>
    public Task SetEnabledAsync(string userName, bool enabled) => RunAsync(async db =>
    {
        var row = await db.Users.FirstOrDefaultAsync(u => u.UserName == userName).ConfigureAwait(false);
        if (row is null) return false;

        row.IsEnabled = enabled;
        await db.SaveChangesAsync().ConfigureAwait(false);
        _logger?.LogWarning("[用户] {Action}账号 {User}", enabled ? "启用" : "停用", userName);
        return true;
    });

    /// <summary>
    /// 实体 → 领域对象。这一层转换存在的意义：界面与 Service 只认 <see cref="UserAccount"/>，
    /// 换存储（或给实体加 EF 专用字段）时不用改上层。
    /// 密码材料原样带出 —— 只读哈希没法校验，但调用方是 Service 层，不是界面。
    /// </summary>
    private static UserAccount ToAccount(UserRecord r) => new()
    {
        UserName = r.UserName,
        DisplayName = r.DisplayName,
        Role = r.Role,
        PasswordHash = r.PasswordHash,
        PasswordSalt = r.PasswordSalt,
        PasswordIterations = r.PasswordIterations,
        IsEnabled = r.IsEnabled,
        MustChangePassword = r.MustChangePassword,
        CreatedAt = r.CreatedAt,
        LastLoginAt = r.LastLoginAt,
    };

    /// <summary>
    /// 领域对象 → 实体（只在"新增"分支用，见 <see cref="UpsertAsync"/>）。
    /// 注意所有字段都得在这里列一遍：漏一个就只在新增时丢值，覆盖分支完全正常，
    /// 这种"只有第一次建账号时错"的 bug 最难在测试里撞到。
    /// </summary>
    private static UserRecord ToRecord(UserAccount a) => new()
    {
        UserName = a.UserName,
        DisplayName = a.DisplayName,
        Role = a.Role,
        PasswordHash = a.PasswordHash,
        PasswordSalt = a.PasswordSalt,
        PasswordIterations = a.PasswordIterations,
        IsEnabled = a.IsEnabled,
        MustChangePassword = a.MustChangePassword,
        CreatedAt = a.CreatedAt,
        LastLoginAt = a.LastLoginAt,
    };
}
