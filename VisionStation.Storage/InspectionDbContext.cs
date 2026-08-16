using Microsoft.EntityFrameworkCore;
using VisionStation.Storage.Entities;

namespace VisionStation.Storage;

public class InspectionDbContext : DbContext
{
    public DbSet<InspectionRecord> InspectionRecords => Set<InspectionRecord>();

    private readonly string _dbPath;

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
            optionsBuilder.UseSqlite($"Data Source={_dbPath}");
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
