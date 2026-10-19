using Microsoft.EntityFrameworkCore;
using VisionStation.Storage.Entities;

namespace VisionStation.Storage;

public class InspectionDbContext : DbContext
{
    public DbSet<InspectionRecord> InspectionRecords => Set<InspectionRecord>();

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
    }
}
