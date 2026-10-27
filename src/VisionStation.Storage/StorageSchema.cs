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
///
/// <para>
/// <b>迁移策略（按代码写实，别按常识猜）</b>：本工程**没有**引用 FluentMigrator、
/// 也没有任何迁移版本表 / <c>__MigrationHistory</c>。升级完全靠"每次启动重新比对一遍"：
/// 用 EF 模型当前的定义去问 SQLite 现在有什么（<c>sqlite_master</c> / <c>PRAGMA table_info</c>），
/// 差什么补什么。代价是每次启动多几次元数据查询（几十条记录的量级，可以忽略），
/// 收益是**没有需要人工维护的迁移脚本**，也就不存在"脚本顺序错了"这种事故。
/// </para>
///
/// <para>
/// <b>幂等性</b>：整体可重复执行。建表脚本被改写成 <c>IF NOT EXISTS</c>，
/// 补列前先查现有列名，WAL 重复设只会返回当前值。所以每次启动都无脑跑一遍是安全的 ——
/// 这正是它不需要版本号的原因。
/// </para>
///
/// <para>
/// <b>归属</b>：<c>internal</c> 静态类，只被三个仓储的 <c>EnsureCreatedAsync</c> 调用
/// （检测 / 审计 / 账号），三处调用的是同一份逻辑 —— 谁先启动都行，
/// 第二次跑时所有比对都"什么都不缺"，直接返回。
/// </para>
///
/// <para>
/// <b>失败行为</b>：抛。<c>ALTER TABLE</c> 失败或库不可写时不能假装成功 ——
/// 结构不对，后面每次写入都会失败，早抛比晚抛少一堆脏数据。
/// </para>
/// </summary>
internal static class StorageSchema
{
    // 表名/列名都不能参数化（DDL 与 PRAGMA 不支持占位符），但它们来自 EF 模型、不接受外部输入。
    // 这一点是本文件唯一的"注入安全"前提，加新语句前先确认参数来源仍是模型而不是配置/文件名。
    private const string AddColumnSql = "ALTER TABLE \"{0}\" ADD COLUMN \"{1}\" {2} {3}";
    private const string TableInfoSql = "PRAGMA table_info(\"{0}\");";
    private static readonly CompositeFormat AddColumnFormat = CompositeFormat.Parse(AddColumnSql);
    private static readonly CompositeFormat TableInfoFormat = CompositeFormat.Parse(TableInfoSql);

    /// <summary>
    /// 确保库、WAL、表结构、列结构四者与当前代码一致。返回实际改动，供调用方记日志。
    ///
    /// <para>
    /// 顺序不能反：先 <c>EnsureCreated</c> 保证库文件在（后面三步都要连库），
    /// 再开 WAL（之后的 DDL 就都在 WAL 模式下执行，不会留下一段 journal 模式的中间态），
    /// 然后补表、最后补列 —— 列是补在表里的，表还不存在时补列只会报 no such table。
    /// </para>
    /// <para>
    /// 返回值只用于日志：<c>JournalMode</c> 正常是 <c>wal</c>，若现场看到别的值
    /// （例如目录只读、或 db 放在网络盘上导致 WAL 建不出来），日志里那一行就是第一条线索。
    /// </para>
    /// </summary>
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
    ///
    /// <para>
    /// <b>注意这里的粒度</b>：只要缺一张表，执行的就是**整库**建表脚本（不是只建那一张）。
    /// 靠 <c>IF NOT EXISTS</c> 保证已有的表不受影响，所以是安全的，但这也意味着
    /// 现场日志里看到"新建缺失表 AuditLogs"时，脚本其实把 InspectionRecords 的建表语句也跑了一遍（被跳过）。
    /// </para>
    /// <para>
    /// 只在真缺表时才生成脚本（<c>missing.Count == 0</c> 提前返回）：生成整库脚本要遍历模型，
    /// 每次启动都白跑一遍没有意义。
    /// </para>
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
    /// 三条替换覆盖 EF 为 SQLite 生成的三种 CREATE 语句（表 / 索引 / 唯一索引）。
    /// 顺序按"先长后短"排：<c>"CREATE INDEX \""</c> 不是 <c>"CREATE UNIQUE INDEX \""</c> 的子串，
    /// 所以眼下怎么排都不会互相误伤，但将来若加 <c>CREATE TEMP TABLE</c> 之类的语句类型，
    /// 从具体到笼统的顺序更不容易漏。
    ///
    /// <para>
    /// 用 <see cref="StringComparison.Ordinal"/> 而不是忽略大小写：脚本是 EF 自己生成的，
    /// 大小写固定，忽略大小写只会扩大误匹配面。
    /// </para>
    /// </summary>
    private static string MakeIdempotent(string script) => script
        .Replace("CREATE UNIQUE INDEX \"", "CREATE UNIQUE INDEX IF NOT EXISTS \"", StringComparison.Ordinal)
        .Replace("CREATE TABLE \"", "CREATE TABLE IF NOT EXISTS \"", StringComparison.Ordinal)
        .Replace("CREATE INDEX \"", "CREATE INDEX IF NOT EXISTS \"", StringComparison.Ordinal);

    /// <summary>
    /// 读现有表名。
    ///
    /// <para>
    /// 自己开关连接（<c>shouldClose</c>）而不是无脑 <c>OpenConnectionAsync</c>：
    /// EF 可能已经把连接开着（同一个 context 里连续几步操作），此时再 Open 会干扰它的状态机；
    /// <c>finally</c> 里也只关自己开的那一次 —— 关掉别人开的连接，下一个查询会拿不到连接。
    /// </para>
    /// <para>
    /// 结果里会包含 <c>sqlite_sequence</c> 这类内部表（自增主键用），无害：
    /// 这里只拿它做"模型里的表在不在"的比较，多出来的名字不影响判断。
    /// </para>
    /// </summary>
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
    ///
    /// <para>
    /// <b>WAL 不是白拿的：它有一个必须知道的代价。</b>WAL 模式下写入先进 <c>-wal</c> 旁路文件，
    /// 只有 checkpoint（把页回写主库）之后那个文件才会被截断。SQLite 默认在 WAL 累积到约 1000 页时
    /// 自动 checkpoint 一次，所以只要没有长事务 / 长读快照挡着，<c>-wal</c> 就维持在几 MB 量级。
    /// </para>
    ///
    /// <para>
    /// <b>真实现场事故：</b><c>visionstation.db-wal</c> 曾经一路涨到 15GB，把工控机盘写满。
    /// 当时的组合是三条同时成立：① 有一个长连接一直开着（旧实现里的全进程单例 DbContext /
    /// 外部数据库工具连着没关）；② 检测在高频写入；③ 长连接不关闭，checkpoint 就一直等不到
    /// "没有活跃读者"的时机，<c>-wal</c> 只增不减。磁盘满之后先坏的是日志和审计，
    /// 然后才是"检测结果写不进去"—— 排障时最难的一步是没人想到去看那个隐藏文件。
    /// </para>
    ///
    /// <para>
    /// <b>所以看到这里要记住两条</b>：① 不要给本库开长连接（DbBrowser 之类查完就断开）；
    /// ② 现场若出现磁盘异常增长，先看 <c>*.db-wal</c> 和 <c>*.db-shm</c> 的体积。
    /// 本方法只负责"确保它是 WAL"，不做定期 checkpoint —— 只要没有长读快照挡着，
    /// SQLite 自己的自动 checkpoint 就够用；一旦将来引入长连接或长事务，必须补一个定时
    /// <c>PRAGMA wal_checkpoint(TRUNCATE)</c>，否则同一个坑会再踩一次。
    /// </para>
    ///
    /// <para>
    /// 失败行为：**不吞异常**。I/O 级错误（库只读、目录不可写）会照常抛，由上层启动流程报错；
    /// 而 SQLite 正常拒绝切换（例如内存库、不支持共享内存的文件系统）不会抛，
    /// 只是把返回的 journal_mode 变成 <c>delete</c> 之类 —— 调用方会把这行写进启动日志。
    /// 那种情况程序照常跑，只是退化成"写的时候读会阻塞"，比"因为开不了 WAL 就起不来"合理。
    /// </para>
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

    /// <summary>
    /// 对比 EF 模型里**每一张表**的属性与库表实际列，缺哪个补哪个。
    ///
    /// <para>
    /// <b>为什么不能只补检测记录表</b>：<see cref="InspectionRecord"/> 只是历史上加列加得最多的表，
    /// 不代表另外两张表不变。<c>AuditLogs</c> / <c>Users</c> 同样会随功能演进加列
    /// （审计加"客户端 IP"、账号加"锁定截止时间"都是迟早的事）。只处理检测表时，
    /// 老库上这两张表的第一次读写就会抛 <c>no such column: Xxx</c>；而开发机用的是新建库，
    /// 列天然存在，本地永远复现不出来 —— 这是最典型的"上线才炸"。
    /// 所以这里按 <c>GetEntityTypes()</c> 遍历全模型，而不是写死一个 <c>FindEntityType</c>。
    /// </para>
    ///
    /// <para>
    /// <b>过滤规则</b>：只处理真正对应物理表的实体 —— 有表名、非 keyless、非 view、
    /// 非 <c>ToSqlQuery</c>、非 JSON 映射。<b>owned type 不单独成表</b>：它的
    /// <c>GetTableName()</c> 就是 owner 的表名，按表名分组后天然合并。
    /// 注意不能因此直接丢掉 owned type 的属性 —— EF Core 8 的
    /// <c>IEntityType.GetProperties()</c> 只返回该实体自己声明的属性，**不包含** owned type 的属性
    /// （已核对 8.0.14 的官方 API 注释：仅返回"this entity type"定义的属性）。
    /// 丢掉它们，将来引入 owned type 又会漏列，所以我们只合并、不单独 ALTER。
    /// </para>
    ///
    /// <para>
    /// <b>每张表各读各的现有列</b>：<see cref="ReadColumnsAsync"/> 是异步的，必须逐表 await，
    /// 不能把所有表的列名并成一个集合再判断 —— 那样 A 表有 X、B 表缺 X 时会被误判成"已存在"，
    /// 于是 B 表仍然缺列。列名比较用 <see cref="StringComparer.OrdinalIgnoreCase"/>。
    /// </para>
    ///
    /// <para>
    /// <b>补出来的列带 NOT NULL DEFAULT（见 <see cref="DefaultOf"/>）</b>：SQLite 往已有数据的表加
    /// 非空列必须给默认值。好处是 ALTER 不会失败；代价是**老记录会静默获得默认值** ——
    /// 一个新加的 <c>bool</c> 列在老数据上全是 <c>false</c>，看起来像"那一批全不合格"。
    /// 所以给老表加列时，要么新列的语义允许"默认 false"，要么在上层按创建时间区分，
    /// 不要假设"列存在"就等于"数据有意义"。
    /// </para>
    ///
    /// <para>
    /// <b>可空列不加 NOT NULL</b>：按 <see cref="Microsoft.EntityFrameworkCore.Metadata.IProperty.IsNullable"/>
    /// 区分。审计表的 <c>Before</c> / <c>After</c> / <c>Detail</c> 都是 <c>string?</c>，
    /// 补成 <c>NOT NULL DEFAULT ''</c> 会让仓储后续写 <c>null</c> 时违反约束 ——
    /// 列"补上了"但审计仍然写不进去，等于没修。可空列因此只声明类型，老行为 NULL。
    /// </para>
    ///
    /// <para>
    /// <b>补列仍然不会补索引</b>：EF 模型里给新列建的索引只出现在"新建整表"的脚本里，
    /// 老库补列之后那个索引并不存在。改成全表遍历**不改变**这一点 —— 查询仍能跑，只是会全表扫。
    /// 加列同时要建索引的话，必须另写一条 <c>CREATE INDEX IF NOT EXISTS</c>，
    /// 别以为补列会顺手带上。
    /// </para>
    ///
    /// <returns>实际补上的列名（调用方只用于日志）。</returns>
    /// </summary>
    private static async Task<List<string>> AddMissingColumnsAsync(InspectionDbContext db)
    {
        var added = new List<string>();

        // 表名 → (列名 → 类型/可空性)。用字典分组而不是"一个实体一次 ALTER"，是因为：
        //   · TPH / 表拆分 / owned type 都会让多个实体落在同一张表上，逐实体处理会对同一张表重复执行；
        //   · 多个实体可能给同一张表贡献不同列，必须先合并再补，否则后一个实体会看不到前一个刚补的列。
        // 两个比较器都用 OrdinalIgnoreCase，与 ReadColumnsAsync 的判重口径一致。
        var expectedByTable = new Dictionary<string, Dictionary<string, (Type ClrType, bool IsNullable)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (string.IsNullOrEmpty(table))
                continue;                                   // 未映射到表：视图 / keyless / JSON owned

            // JSON 映射的列在 owner 的容器列里，不是独立列，不能 ALTER。
            if (entity.IsMappedToJson())
                continue;

            // owned type 不参与 keyless / view 过滤：它本来就映射在 owner 的表里。
            // 非 owned 实体按"是否是物理表"过滤：keyless（含 view、ToSqlQuery）不该 ALTER。
            if (!entity.IsOwned()
                && (entity.FindPrimaryKey() is null
                    || entity.GetViewName() is not null
                    || entity.GetSqlQuery() is not null))
                continue;

            if (!expectedByTable.TryGetValue(table, out var columns))
            {
                columns = new Dictionary<string, (Type, bool)>(StringComparer.OrdinalIgnoreCase);
                expectedByTable[table] = columns;
            }

            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName();
                if (string.IsNullOrEmpty(column))
                    continue;                               // 没有独立列名的属性（JSON 路径等）

                // 已由前面的实体贡献过同名列就保留第一次的类型：同一张表里同名列不可能有两种类型。
                columns.TryAdd(column, (property.ClrType, property.IsNullable));
            }
        }

        foreach (var (table, columns) in expectedByTable)
        {
            // 逐表 await：不能把各表列名混成一个集合，否则"A 表有、B 表没有"会被误判成"已经有了"。
            var actual = await ReadColumnsAsync(db, table).ConfigureAwait(false);

            foreach (var (column, spec) in columns)
            {
                if (actual.Contains(column, StringComparer.OrdinalIgnoreCase))
                    continue;

                // table/col 都来自 EF 模型（编译期常量），不接受外部输入。
                var sql = string.Format(CultureInfo.InvariantCulture, AddColumnFormat,
                    table, column, SqlTypeOf(spec.ClrType), DefaultOf(spec.ClrType, spec.IsNullable));
                await db.Database.ExecuteSqlRawAsync(sql).ConfigureAwait(false);
                added.Add(column);
            }
        }

        return added;
    }

    /// <summary>
    /// 读某张表现有的列名（连接开关的理由同 <see cref="ReadTablesAsync"/>）。
    ///
    /// <para>
    /// 取的是 <c>PRAGMA table_info</c> 的第 2 列。该 PRAGMA 的列顺序是固定的：
    /// <c>cid, name, type, notnull, dflt_value, pk</c> —— 用序号取值而不是按列名找，
    /// 是因为这里只关心名字，多解析一份结果没有收益；但这意味着 SQLite 若改了这个顺序，本方法会静默读错列。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// CLR 类型 → SQLite 列类型。
    ///
    /// SQLite 只有五个存储类、且不强制列类型（动态类型），这里给的是"便于 EF 读写和肉眼判断"的声明：
    /// <c>bool</c> 落成 INTEGER 0/1（SQLite 没有布尔类型，EF 也按 0/1 存），
    /// 浮点/定点落成 REAL（注意 decimal 走 REAL 会丢精度 —— 本工程没存金额，量具数值用 double 够），
    /// 其余（string / DateTime / Guid）都落成 TEXT。
    ///
    /// <para>
    /// <b>枚举按 INTEGER</b>：EF 默认把没有 ValueConverter 的枚举存成 int（审计的 Action/Result、
    /// 用户的 Role 都是这样）。若按 <see cref="Type.IsEnum"/> 落到 TEXT，SQLite 的 TEXT 亲和性
    /// 会把写入的整数转成文本，声明类型与 EF 的读写方式不一致。
    /// </para>
    /// <para>
    /// 先 <see cref="Nullable.GetUnderlyingType"/> 解包：<c>int?</c> 仍然是 INTEGER，<c>DateTime?</c>
    /// 仍然是 TEXT。不解包的话可空数值列会被误声明成 TEXT。
    /// </para>
    /// </summary>
    private static string SqlTypeOf(Type t)
    {
        var underlying = Nullable.GetUnderlyingType(t) ?? t;

        if (underlying.IsEnum
            || underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short)
            || underlying == typeof(bool))
            return "INTEGER";
        if (underlying == typeof(double) || underlying == typeof(float) || underlying == typeof(decimal))
            return "REAL";
        return "TEXT";
    }

    /// <summary>
    /// 新增列的默认值子句。
    ///
    /// <para>
    /// <b>可空列（<paramref name="isNullable"/> = true）只写 <c>NULL</c></b>：SQLite 允许往已有数据的表
    /// 加无默认值的可空列，老行拿到 NULL，与新建库（EF 建表时该列就是 NULL）一致。
    /// 若也给可空列写 <c>NOT NULL DEFAULT ...</c>，仓储之后写 null 会违反约束 ——
    /// 审计的 <c>Before</c> / <c>After</c> / <c>Detail</c> 正是这种列，列补上了却仍然写不进去。
    /// </para>
    ///
    /// <para>
    /// <b>非空列写成 <c>NOT NULL DEFAULT ...</c></b>：SQLite 的 <c>ALTER TABLE ADD COLUMN</c>
    /// 在已有数据的表上要求新列可空或带默认值，否则直接报错。给默认值能让升级一步成功，
    /// 代价是**老行会拿到这个值**（见 <see cref="AddMissingColumnsAsync"/> 的说明）。
    /// </para>
    /// <para>
    /// 认不出的类型一律 <c>NULL</c>（即允许为空）：宁可让列比模型宽松，也不要为了让 DDL 漂亮
    /// 而猜一个默认值 —— 猜错的默认值会变成"看起来有数据、其实是编的"，比空着更难发现。
    /// 枚举按 INTEGER 处理、默认 0：与 EF 的默认映射一致（见 <see cref="SqlTypeOf"/>），
    /// 否则老行是 NULL、非空枚举读出来会抛 <c>Nullable object must have a value</c>。
    /// </para>
    /// </summary>
    private static string DefaultOf(Type t, bool isNullable)
    {
        // 可空列不需要默认值；Nullable<T> 也走这一支（ClrType 是 Nullable<Xxx>，下面的类型判断都不匹配）。
        if (isNullable)
            return "NULL";

        var underlying = Nullable.GetUnderlyingType(t) ?? t;

        if (underlying.IsEnum)
            return "NOT NULL DEFAULT 0";
        if (underlying == typeof(string))
            return "NOT NULL DEFAULT ''";
        if (underlying == typeof(DateTime))
            return "NOT NULL DEFAULT '0001-01-01 00:00:00'";
        if (underlying == typeof(Guid))
            return "NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'";
        if (underlying == typeof(bool) || underlying == typeof(int) || underlying == typeof(long)
            || underlying == typeof(short) || underlying == typeof(double) || underlying == typeof(float)
            || underlying == typeof(decimal))
            return "NOT NULL DEFAULT 0";
        return "NULL";
    }
}
