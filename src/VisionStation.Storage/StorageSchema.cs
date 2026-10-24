using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VisionStation.Storage.Entities;

namespace VisionStation.Storage;

/// <summary>
/// 轻量结构升级器。
///
/// 为什么需要它：<c>Database.EnsureCreated()</c> 的语义是「库不存在就整库创建」，
/// 对已存在的库不做任何 ALTER TABLE。给实体加一列，现场老库的表结构不会跟着变，
/// 第一条保存就抛 "no such column" —— 而开发机（新库）完全复现不出来。
///
/// 同一个坑的另一种形态是**加新表**：EnsureCreated 同样不会给已有的库建表。
/// 2026-10-24 加 AuditLogs 时就会踩到 —— 开发机删库重建一切正常，现场老库
/// 第一条审计抛 "no such table"。所以这里管三件事：库在、表齐、列齐。
///
/// 只做「补缺失的表/列」，不做删列 / 改类型：后者要重建表，属迁移工具的职责。
/// 补表补列是唯一能安全自动化的形态 —— 都是纯追加，不动已有数据。
/// </summary>
internal static class StorageSchema
{
    // 表名/列名都不能参数化（DDL 与 PRAGMA 不支持占位符），但它们来自 EF 模型、不接受外部输入。
    private const string AddColumnSql = "ALTER TABLE \"{0}\" ADD COLUMN \"{1}\" {2} {3}";
    private const string TableInfoSql = "PRAGMA table_info(\"{0}\");";
    private static readonly CompositeFormat AddColumnFormat = CompositeFormat.Parse(AddColumnSql);
    private static readonly CompositeFormat TableInfoFormat = CompositeFormat.Parse(TableInfoSql);

    /// <summary>确保库、WAL、表结构、列结构四者与当前代码一致。返回实际改动，供调用方记日志。</summary>
    public static async Task<(string JournalMode, IReadOnlyList<string> AddedTables, IReadOnlyList<string> AddedColumns)>
        EnsureUpToDateAsync(InspectionDbContext db)
    {
        await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
        var journal = await EnableWalAsync(db).ConfigureAwait(false);
        var addedTables = await AddMissingTablesAsync(db).ConfigureAwait(false);
        var addedColumns = await AddMissingColumnsAsync(db).ConfigureAwait(false);
        return (journal, addedTables, addedColumns);
    }

    /// <summary>
    /// 给已存在的库补上新增实体对应的表（连同它的索引）。
    ///
    /// 用 EF 从当前模型生成的建库脚本，把 CREATE 改成 IF NOT EXISTS：
    /// 已有的表/索引被跳过，缺的建出来。这样表定义永远跟着实体走，
    /// 不会出现"手写 DDL 忘了同步新增字段"的漂移。
    /// </summary>
    private static async Task<List<string>> AddMissingTablesAsync(InspectionDbContext db)
    {
        var expected = db.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var actual = await ReadTablesAsync(db).ConfigureAwait(false);
        var missing = expected
            .Where(t => !actual.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // 表齐了就别执行脚本 —— 建库脚本是整库的，白跑一遍没必要。
        if (missing.Count == 0) return missing;

        var script = db.Database.GenerateCreateScript();
        if (string.IsNullOrWhiteSpace(script)) return missing;

        await db.Database.ExecuteSqlRawAsync(MakeIdempotent(script)).ConfigureAwait(false);
        return missing;
    }

    /// <summary>
    /// 把 EF 生成的建库脚本改成可重复执行的。
    ///
    /// 顺序要紧：先改 UNIQUE INDEX，再改 TABLE/INDEX —— 反过来的话
    /// "CREATE INDEX" 的替换会误伤 "CREATE UNIQUE INDEX" 吗？不会（子串不匹配），
    /// 但先长后短更不容易在将来加语句类型时出错。
    /// </summary>
    private static string MakeIdempotent(string script) => script
        .Replace("CREATE UNIQUE INDEX \"", "CREATE UNIQUE INDEX IF NOT EXISTS \"", StringComparison.Ordinal)
        .Replace("CREATE TABLE \"", "CREATE TABLE IF NOT EXISTS \"", StringComparison.Ordinal)
        .Replace("CREATE INDEX \"", "CREATE INDEX IF NOT EXISTS \"", StringComparison.Ordinal);

    private static async Task<List<string>> ReadTablesAsync(InspectionDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose)
            await db.Database.OpenConnectionAsync().ConfigureAwait(false);

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            var tables = new List<string>();
            while (await reader.ReadAsync().ConfigureAwait(false))
                tables.Add(reader.GetString(0));
            return tables;
        }
        finally
        {
            if (shouldClose)
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 开 WAL：让读不被写阻塞。
    ///
    /// SQLite 单写者的事实改不了（写-写仍然排队），但 rollback journal 模式下
    /// 连读都要等写提交 —— 清理一轮删几万条时，UI 的「最近记录」列表会一起卡住。
    /// journal_mode 是写进库文件的持久设置，设一次即可，重复设只是返回当前值。
    /// </summary>
    private static async Task<string> EnableWalAsync(InspectionDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose)
            await db.Database.OpenConnectionAsync().ConfigureAwait(false);

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            var raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "unknown";
        }
        finally
        {
            if (shouldClose)
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>对比 EF 模型属性与库表实际列，缺哪个补哪个。</summary>
    private static async Task<List<string>> AddMissingColumnsAsync(InspectionDbContext db)
    {
        var added = new List<string>();
        var entity = db.Model.FindEntityType(typeof(InspectionRecord));
        var table = entity?.GetTableName();
        if (entity is null || string.IsNullOrEmpty(table))
            return added;

        var expected = entity.GetProperties()
            .Select(p => (Name: p.GetColumnName(), ClrType: p.ClrType))
            .Where(p => !string.IsNullOrEmpty(p.Name))
            .ToList();

        var actual = await ReadColumnsAsync(db, table).ConfigureAwait(false);

        foreach (var col in expected)
        {
            if (actual.Contains(col.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            // table/col 都来自 EF 模型（编译期常量），不接受外部输入。
            var sql = string.Format(CultureInfo.InvariantCulture, AddColumnFormat,
                table, col.Name, SqlTypeOf(col.ClrType), DefaultOf(col.ClrType));
            await db.Database.ExecuteSqlRawAsync(sql).ConfigureAwait(false);
            added.Add(col.Name);
        }

        return added;
    }

    private static async Task<List<string>> ReadColumnsAsync(InspectionDbContext db, string table)
    {
        var conn = db.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose)
            await db.Database.OpenConnectionAsync().ConfigureAwait(false);

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = string.Format(CultureInfo.InvariantCulture, TableInfoFormat, table);
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            var cols = new List<string>();
            while (await reader.ReadAsync().ConfigureAwait(false))
                cols.Add(reader.GetString(1));      // 第 2 列是列名
            return cols;
        }
        finally
        {
            if (shouldClose)
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static string SqlTypeOf(Type t)
    {
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(bool))
            return "INTEGER";
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal))
            return "REAL";
        return "TEXT";
    }

    private static string DefaultOf(Type t)
    {
        if (t == typeof(string))
            return "NOT NULL DEFAULT ''";
        if (t == typeof(DateTime))
            return "NOT NULL DEFAULT '0001-01-01 00:00:00'";
        if (t == typeof(Guid))
            return "NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'";
        if (t == typeof(bool) || t == typeof(int) || t == typeof(long) || t == typeof(short)
            || t == typeof(double) || t == typeof(float) || t == typeof(decimal))
            return "NOT NULL DEFAULT 0";
        return "NULL";
    }
}
