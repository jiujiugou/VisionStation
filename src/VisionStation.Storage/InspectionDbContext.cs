using Microsoft.EntityFrameworkCore;
using VisionStation.Storage.Entities;

namespace VisionStation.Storage;

public class InspectionDbContext : DbContext
{
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

    private readonly string _dbPath = string.Empty;

    public InspectionDbContext(string dbPath = "visionstation.db")
    {
        _dbPath = dbPath;
    }

    public InspectionDbContext(DbContextOptions<InspectionDbContext> options)
        : base(options)
    {
    }

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
    }
}
