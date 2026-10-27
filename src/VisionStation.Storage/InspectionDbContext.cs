using Microsoft.EntityFrameworkCore;
using VisionStation.Storage.Entities;

namespace VisionStation.Storage;

/// <summary>
/// 全库唯一的 EF Core 上下文：检测记录 / 审计流水 / 用户账号三张表，**同一个 SQLite 文件**。
///
/// <para>
/// <b>定位</b>：Storage 层的最底层。三个仓储（<c>InspectionRepository</c> /
/// <c>AuditRepository</c> / <c>UserRepository</c>）是它仅有的使用者，
/// 它们不直接暴露本类型，上层（Service / App）看不到 EF。
/// </para>
///
/// <para>
/// <b>归属：每次操作 new 一个，用完就 Dispose</b>。全程序**没有**单例 DbContext，也不该有 ——
/// EF Core 的 DbContext 不是线程安全的，一个实例被两个线程同时碰会抛
/// "a second operation was started on this context"，而且它内部会缓存实体（ChangeTracker），
/// 活得越久内存涨得越多。三个仓储各自持有一个 <c>() => new InspectionDbContext(path)</c> 的工厂，
/// 每个方法调用现开现关；测试则用接受 <c>DbContextOptions</c> 的那个构造注入一次建好的库。
/// </para>
///
/// <para>
/// <b>失败行为</b>：构造函数不连库（SQLite 是打开即用的文件库，连不上要到第一次查询才知道）。
/// 之后的错误以 <c>SqliteException</c>（查询/DDL）或 <c>DbUpdateException</c>（写入）抛出，
/// 本类不捕获、不翻译 —— 翻译在仓储层做（只有"库被锁"那一句值得翻成人话）。
/// </para>
/// </summary>
public class InspectionDbContext : DbContext
{
    /// <summary>
    /// 检测记录。写入口只有 <c>InspectionRepository.SaveAsync</c>（纯追加），
    /// 读入口是历史页的带筛选查询；保留期由管家按 <c>CreatedAt</c> 批量删。
    /// 没有 Update 路径 —— 一条记录落库就是既成事实。
    /// </summary>
    public DbSet<InspectionRecord> InspectionRecords => Set<InspectionRecord>();

    /// <summary>
    /// 审计流水。与检测记录同一套库文件、同一套 schema 升级器。
    ///
    /// 为什么不分库：审计与检测在同一个 SQLite 文件里，好处是"备份一个文件就是完整证据"，
    /// 坏处是审计写入会和检测保存抢同一个写锁。后者由 WAL + 后台队列消化
    /// （见 AuditService），不是靠分库解决。
    /// </summary>
    public DbSet<AuditLogRecord> AuditLogs => Set<AuditLogRecord>();

    /// <summary>
    /// 用户账号。单机工控机的账号表通常只有个位数行 —— 但必须入库而不是放配置文件：
    /// 密码哈希、停用状态、最后登录时间都要留痕，写进 JSON 等于谁都能改。
    /// </summary>
    public DbSet<UserRecord> Users => Set<UserRecord>();

    /// <summary>
    /// 数据库文件路径（只给 <see cref="OnConfiguring"/> 拼连接串用）。字段初值是空串，
    /// 真正的默认值在下面那个构造函数的参数上。
    ///
    /// <para>
    /// 注意构造参数的默认值 <c>visionstation.db</c> 是**相对路径**，会落到进程的当前工作目录 ——
    /// 快捷方式"起始位置"、计划任务、从资源管理器双击都可能把 cwd 指到别处，
    /// 那等于每次启动换一个空库。生产必须传 <c>AppPaths.DbPath</c>（已在构造时解析成绝对路径），
    /// 默认值只服务于"随手 new 一个来试"和测试。
    /// </para>
    /// </summary>
    private readonly string _dbPath = string.Empty;

    /// <summary>
    /// 生产构造：只给路径，连接串细节交给 <see cref="OnConfiguring"/>。
    /// 每次操作 new 一个，由仓储在 <c>finally</c> 里 Dispose。
    /// </summary>
    public InspectionDbContext(string dbPath = "visionstation.db")
    {
        _dbPath = dbPath;
    }

    /// <summary>
    /// 外部已配好选项的构造（测试建临时库 / 内存库，或将来接入 DI 时用）。
    ///
    /// 走这条路时 <see cref="OnConfiguring"/> 里的 <c>IsConfigured</c> 为 true，
    /// 不会再拼连接串 —— 也就**不会**带上 <c>Default Timeout=5</c>，
    /// 用的是 Microsoft.Data.Sqlite 的默认 30 秒。测试里遇到锁等待会明显变慢，这是预期内的差别。
    /// </summary>
    public InspectionDbContext(DbContextOptions<InspectionDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// 只在没人配过选项时生效（生产那条构造）。连接串里只有两样东西：
    /// 数据文件路径，以及忙碌超时。
    ///
    /// <para>
    /// <b>没有设置 <c>Pooling</c>、<c>Cache</c>、<c>Mode</c></b>：连接池由 Microsoft.Data.Sqlite 默认开启，
    /// 这里不额外调参 —— 每帧新建连接的开销已经由池化吸收，
    /// 再手工调参只会让"为什么这台机器上行为不一样"多一个变量。
    /// </para>
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            // Default Timeout 控制的是「遇到 SQLITE_BUSY 时最多重试多久」：
            // Microsoft.Data.Sqlite 的默认值是 30 秒，意味着清理一轮删几万条时，
            // 保存线程可能在 UI 上卡满 30 秒才报错。5 秒足够覆盖正常的锁竞争窗口，
            // 超过就显式失败（失败可见），而不是无声卡住。
            optionsBuilder.UseSqlite($"Data Source={_dbPath};Default Timeout=5");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InspectionRecord>(entity =>
        {
            // 三个索引各自对应一条真实查询，没有一个是"顺手加的"：
            //   Timestamp      —— 历史页的时间范围筛选 + OrderByDescending(Timestamp)（最常用）
            //   OverallVerdict —— "只看 NG"筛选、今日良率的 GroupBy
            //   CreatedAt      —— 管家按保留期删除（WHERE CreatedAt < cutoff）
            // 没给 EngineName 建索引：它只用在历史页的引擎下拉（全表 DISTINCT）和精确筛选，
            // 一天几十条的量级下加索引只是白白放大写入成本。
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.OverallVerdict);
            entity.HasIndex(e => e.CreatedAt);
        });

        // 三个索引对应三种真实查法：
        //   Timestamp —— "昨天下午三点到四点发生了什么"（复盘最常用）
        //   UserName  —— "这批料是谁当班"（追责）
        //   Action    —— "所有急停记录"/"所有登录失败"（安全审查）
        // 组合索引不建：数据量小（一天几十条），单列足够，多建只是写放大。
        modelBuilder.Entity<AuditLogRecord>(entity =>
        {
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.UserName);
            entity.HasIndex(e => e.Action);
        });

        // 用户表这里不配：主键、列宽全靠实体上的 [Key] / [MaxLength]，
        // EF 按约定生成即可。加一条空配置只会让人以为那里有特殊映射。
    }
}
