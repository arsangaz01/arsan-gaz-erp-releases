using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ArsanGazERP.Data;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;

// Copies the ERP and CRM SQLite databases into the PostgreSQL schemas erp / crm and verifies the result.
// The production files are never written: each one is first copied with the SQLite online backup API into a
// temporary snapshot, and only the snapshot is read.
//
//   ArsanGazERP.DbMigrator [--replace] [--verify-only] [--erp-db <path>] [--crm-db <path>] [--report <file>]
//
// The PostgreSQL connection comes from ARSANGAZ_PG_CONNECTION or the DPAPI-protected db-connection.json.
Environment.SetEnvironmentVariable("ARSANGAZ_DB_PROVIDER", "postgres");
Console.OutputEncoding = Encoding.UTF8;

bool replace = args.Contains("--replace", StringComparer.Ordinal);
bool verifyOnly = args.Contains("--verify-only", StringComparer.Ordinal);
string erpSource = Option("--erp-db") ?? DefaultSqlitePath(DatabaseKind.Erp);
string crmSource = Option("--crm-db") ?? DefaultSqlitePath(DatabaseKind.Crm);
string? reportPath = Option("--report");

string snapshotDirectory = Path.Combine(Path.GetTempPath(), "arsangaz-migrate-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
Directory.CreateDirectory(snapshotDirectory);
try
{
    Console.WriteLine($"Hedef: {ArsanDatabase.Describe(DatabaseKind.Erp)} / {ArsanDatabase.Describe(DatabaseKind.Crm)}");
    string erpSnapshot = Migrator.Snapshot(erpSource, Path.Combine(snapshotDirectory, "erp.db"));
    string crmSnapshot = Migrator.Snapshot(crmSource, Path.Combine(snapshotDirectory, "crm.db"));
    Console.WriteLine("SQLite anlık kopyaları alındı (yedekleme API'si, üretim dosyalarına yazılmadı).");

    if (!verifyOnly)
    {
        await Migrator.EnsureSchemasAsync();
        await Migrator.CopyAsync([(DatabaseKind.Erp, erpSnapshot), (DatabaseKind.Crm, crmSnapshot)], replace);
    }

    List<VerificationRow> rows = [];
    List<string> notes = [];
    rows.AddRange(await Migrator.VerifyAsync(DatabaseKind.Erp, erpSnapshot, notes));
    rows.AddRange(await Migrator.VerifyAsync(DatabaseKind.Crm, crmSnapshot, notes));
    string report = Migrator.Render(rows, notes);
    Console.WriteLine(report);
    if (reportPath is not null)
    {
        await File.WriteAllTextAsync(reportPath, report, new UTF8Encoding(false));
    }

    bool ok = rows.All(row => row.Ok);
    Console.WriteLine(ok ? "SONUÇ: TÜM TABLOLAR EŞLEŞTİ" : "SONUÇ: FARK VAR");
    return ok ? 0 : 2;
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(snapshotDirectory, recursive: true);
}

string? Option(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string DefaultSqlitePath(DatabaseKind kind) => kind == DatabaseKind.Erp
    ? Environment.GetEnvironmentVariable("ARSANGAZ_ERP_DB") is { Length: > 0 } erp ? erp
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "Data", "arsangaz-erp-v3.db")
    : Environment.GetEnvironmentVariable("ARSANGAZ_CRM_DB") is { Length: > 0 } crm ? crm
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "ArsanGazERP", "CRM", "arsangaz-crm.db");

internal sealed record PgColumn(string Name, string DataType, bool Nullable);

internal sealed record PgTable(string Name, List<PgColumn> Columns, List<string> PrimaryKey, HashSet<string> References);

internal sealed record VerificationRow(string Schema, string Table, long SourceRows, long TargetRows, string Sums, bool SumsOk, string SourceHash, string TargetHash)
{
    public bool Ok => SourceRows == TargetRows && SumsOk && SourceHash == TargetHash;
}

internal static partial class Migrator
{
    private static readonly HashSet<string> SkippedTables = new(StringComparer.OrdinalIgnoreCase) { "__EFMigrationsHistory", "sqlite_sequence" };

    public static string Snapshot(string source, string destination)
    {
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("SQLite veritabanı bulunamadı: " + source);
        }

        using SqliteConnection from = new(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using SqliteConnection to = new(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        from.Open();
        to.Open();
        from.BackupDatabase(to);
        using SqliteCommand check = to.CreateCommand();
        check.CommandText = "PRAGMA quick_check;";
        string result = Convert.ToString(check.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{source} kopyası bütünlük denetiminden geçmedi: {result}");
        }

        return destination;
    }

    public static async Task EnsureSchemasAsync()
    {
        await using (ArsanGazDbContext erp = new())
        {
            await ArsanDbEf.EnsurePostgresSchemaAsync(erp, DatabaseKind.Erp);
        }

        await using (CrmDbContext crm = new())
        {
            await ArsanDbEf.EnsurePostgresSchemaAsync(crm, DatabaseKind.Crm);
        }

        await using DbConnection connection = await ArsanDatabase.OpenPostgresAsync(DatabaseKind.Crm);
        await using DbCommand campaign = ArsanDatabase.Command(connection, null, PostgresSchema.Campaign);
        await campaign.ExecuteNonQueryAsync();
    }

    public static async Task CopyAsync(IReadOnlyList<(DatabaseKind Kind, string Snapshot)> sources, bool replace)
    {
        await using NpgsqlConnection connection = (NpgsqlConnection)await ArsanDatabase.OpenPostgresAsync(DatabaseKind.Erp);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        foreach ((DatabaseKind kind, _) in sources)
        {
            string schema = ArsanDatabase.Schema(kind);
            List<PgTable> tables = await ReadPgTablesAsync(connection, transaction, schema);
            long existing = 0;
            foreach (PgTable table in tables)
            {
                existing += await ScalarAsync(connection, transaction, $"SELECT COUNT(*) FROM {schema}.{table.Name};");
            }

            if (existing > 0 && !replace)
            {
                throw new InvalidOperationException($"{schema} şemasında {existing} satır var. Üzerine yazmak için --replace kullanın.");
            }

            if (existing > 0)
            {
                string all = string.Join(", ", tables.Select(table => $"{schema}.{table.Name}"));
                await ExecuteAsync(connection, transaction, $"TRUNCATE TABLE {all} RESTART IDENTITY CASCADE;");
                Console.WriteLine($"{schema}: mevcut {existing} satır silindi (--replace).");
            }
        }

        foreach ((DatabaseKind kind, string snapshot) in sources)
        {
            string schema = ArsanDatabase.Schema(kind);
            List<PgTable> tables = TopologicalOrder(await ReadPgTablesAsync(connection, transaction, schema));
            await using SqliteConnection source = OpenSnapshot(snapshot);
            Dictionary<string, string> sourceTables = SqliteTables(source);
            foreach (PgTable table in tables)
            {
                if (!sourceTables.TryGetValue(table.Name, out string? sourceName))
                {
                    continue;
                }

                SourceTable data = ReadSource(source, sourceName, table);
                if (data.Rows.Count == 0)
                {
                    continue;
                }

                string columns = string.Join(", ", data.Columns.Select(column => column.Name));
                await using (NpgsqlBinaryImporter importer = await connection.BeginBinaryImportAsync(
                    $"COPY {schema}.{table.Name} ({columns}) FROM STDIN (FORMAT BINARY)"))
                {
                    foreach (object?[] row in data.Rows)
                    {
                        await importer.StartRowAsync();
                        for (int i = 0; i < row.Length; i++)
                        {
                            if (row[i] is null)
                            {
                                await importer.WriteNullAsync();
                            }
                            else
                            {
                                await importer.WriteAsync(row[i], DbType(data.Columns[i].DataType));
                            }
                        }
                    }

                    await importer.CompleteAsync();
                }

                Console.WriteLine($"{schema}.{table.Name}: {data.Rows.Count} satır kopyalandı.");
            }

            await ResetIdentitiesAsync(connection, transaction, schema);
        }

        await transaction.CommitAsync();
    }

    public static async Task<List<VerificationRow>> VerifyAsync(DatabaseKind kind, string snapshot, List<string> notes)
    {
        string schema = ArsanDatabase.Schema(kind);
        await using NpgsqlConnection connection = (NpgsqlConnection)await ArsanDatabase.OpenPostgresAsync(kind, readOnly: true);
        List<PgTable> tables = await ReadPgTablesAsync(connection, null, schema);
        await using SqliteConnection source = OpenSnapshot(snapshot);
        Dictionary<string, string> sourceTables = SqliteTables(source);
        HashSet<string> pgNames = tables.Select(table => table.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach ((string lower, string name) in sourceTables.Where(pair => !pgNames.Contains(pair.Key)))
        {
            notes.Add($"{schema}: SQLite tablosu '{name}' PostgreSQL'de yok, kopyalanmadı ({CountSqlite(source, name)} satır).");
        }

        List<VerificationRow> rows = [];
        foreach (PgTable table in tables.OrderBy(table => table.Name, StringComparer.Ordinal))
        {
            SourceTable data = sourceTables.TryGetValue(table.Name, out string? sourceName)
                ? ReadSource(source, sourceName, table, notes, schema)
                : new SourceTable([], []);
            List<object?[]> target = await ReadTargetAsync(connection, schema, table, data.Columns);
            (string sums, bool sumsOk) = CompareSums(table, data, target);
            rows.Add(new VerificationRow(schema, table.Name, data.Rows.Count, target.Count, sums, sumsOk,
                Hash(table, data.Columns, data.Rows), Hash(table, data.Columns, target)));
        }

        return rows;
    }

    public static string Render(List<VerificationRow> rows, List<string> notes)
    {
        StringBuilder builder = new();
        builder.AppendLine("| Şema | Tablo | SQLite satır | PG satır | Tutar/miktar toplamları | İçerik özeti | Sonuç |");
        builder.AppendLine("|---|---|---:|---:|---|---|---|");
        foreach (VerificationRow row in rows)
        {
            string hash = row.SourceHash == row.TargetHash ? row.SourceHash[..12] : $"{row.SourceHash[..8]}≠{row.TargetHash[..8]}";
            builder.AppendLine($"| {row.Schema} | {row.Table} | {row.SourceRows} | {row.TargetRows} | {row.Sums} | {hash} | {(row.Ok ? "OK" : "FARK")} |");
        }

        builder.AppendLine();
        builder.AppendLine($"Toplam: {rows.Count} tablo, {rows.Sum(row => row.SourceRows)} kaynak satır, {rows.Sum(row => row.TargetRows)} hedef satır, {rows.Count(row => !row.Ok)} farklı tablo.");
        foreach (string note in notes.Distinct())
        {
            builder.AppendLine("Not: " + note);
        }

        return builder.ToString();
    }

    private sealed record SourceTable(List<PgColumn> Columns, List<object?[]> Rows);

    private static SourceTable ReadSource(SqliteConnection source, string sourceName, PgTable table, List<string>? notes = null, string? schema = null)
    {
        List<string> sourceColumns = [];
        using (SqliteCommand info = source.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info(\"{sourceName}\");";
            using SqliteDataReader reader = info.ExecuteReader();
            while (reader.Read())
            {
                sourceColumns.Add(reader.GetString(1));
            }
        }

        Dictionary<string, string> byLower = sourceColumns.ToDictionary(name => name.ToLowerInvariant(), StringComparer.Ordinal);
        List<PgColumn> columns = table.Columns.Where(column => byLower.ContainsKey(column.Name)).ToList();
        if (notes is not null)
        {
            foreach (string missing in sourceColumns.Where(name => table.Columns.All(column => column.Name != name.ToLowerInvariant())))
            {
                long nonNull = CountSqlite(source, sourceName, $"\"{missing}\" IS NOT NULL");
                notes.Add($"{schema}.{table.Name}: SQLite sütunu '{missing}' PostgreSQL'de yok ({nonNull} dolu değer kopyalanmadı).");
            }

            foreach (PgColumn missing in table.Columns.Where(column => !byLower.ContainsKey(column.Name)))
            {
                notes.Add($"{schema}.{table.Name}: PostgreSQL sütunu '{missing.Name}' SQLite'ta yok (varsayılan değer kullanıldı).");
            }
        }

        // SQLite does not enforce foreign keys on these files; rows whose parent no longer exists cannot be inserted
        // into PostgreSQL and are left out (and reported).
        HashSet<long> orphans = [];
        using (SqliteCommand check = source.CreateCommand())
        {
            check.CommandText = $"PRAGMA foreign_key_check(\"{sourceName}\");";
            using SqliteDataReader reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                {
                    orphans.Add(reader.GetInt64(1));
                }
            }
        }

        if (notes is not null && orphans.Count > 0)
        {
            notes.Add($"{schema}.{table.Name}: üst kaydı (yabancı anahtar hedefi) olmayan {orphans.Count} yetim satır kopyalanmadı (SQLite rowid: {string.Join(", ", orphans.Order())}); SQLite satır sayısı bunlar hariçtir.");
        }

        List<object?[]> rows = [];
        using SqliteCommand select = source.CreateCommand();
        select.CommandText = $"SELECT {string.Join(", ", columns.Select(column => $"\"{byLower[column.Name]}\"").Prepend("rowid"))} FROM \"{sourceName}\";";
        using (SqliteDataReader reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                if (orphans.Contains(reader.GetInt64(0)))
                {
                    continue;
                }

                object?[] row = new object?[columns.Count];
                for (int i = 0; i < columns.Count; i++)
                {
                    row[i] = ConvertValue(reader.IsDBNull(i + 1) ? null : reader.GetValue(i + 1), columns[i], table.Name);
                }

                rows.Add(row);
            }
        }

        return new SourceTable(columns, rows);
    }

    private static async Task<List<object?[]>> ReadTargetAsync(NpgsqlConnection connection, string schema, PgTable table, List<PgColumn> columns)
    {
        List<object?[]> rows = [];
        if (columns.Count == 0)
        {
            long count = await ScalarAsync(connection, null, $"SELECT COUNT(*) FROM {schema}.{table.Name};");
            for (long i = 0; i < count; i++)
            {
                rows.Add([]);
            }

            return rows;
        }

        await using NpgsqlCommand command = new($"SELECT {string.Join(", ", columns.Select(column => column.Name))} FROM {schema}.{table.Name};", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            object?[] row = new object?[columns.Count];
            for (int i = 0; i < columns.Count; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    // SQLite stores EF DateTime as "yyyy-MM-dd HH:mm:ss.fffffff" local wall-clock text; values with an explicit
    // offset (written by raw SQL as UTC ISO-8601) are converted to their UTC wall-clock time.
    private static object? ConvertValue(object? value, PgColumn column, string table)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return column.DataType switch
            {
                "boolean" => value switch
                {
                    long number => number != 0,
                    string text => text.Trim() is "1" || text.Trim().Equals("true", StringComparison.OrdinalIgnoreCase),
                    _ => System.Convert.ToBoolean(value, CultureInfo.InvariantCulture)
                },
                "smallint" => System.Convert.ToInt16(value, CultureInfo.InvariantCulture),
                "integer" => System.Convert.ToInt32(value, CultureInfo.InvariantCulture),
                "bigint" => System.Convert.ToInt64(value, CultureInfo.InvariantCulture),
                "numeric" => value is string text
                    ? decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
                    : System.Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                "double precision" => System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
                "real" => System.Convert.ToSingle(value, CultureInfo.InvariantCulture),
                "timestamp without time zone" => ParseTimestamp(System.Convert.ToString(value, CultureInfo.InvariantCulture)!),
                "timestamp with time zone" => DateTime.SpecifyKind(ParseTimestamp(System.Convert.ToString(value, CultureInfo.InvariantCulture)!), DateTimeKind.Utc),
                "date" => ParseTimestamp(System.Convert.ToString(value, CultureInfo.InvariantCulture)!).Date,
                "text" or "character varying" or "character" => value as string ?? System.Convert.ToString(value, CultureInfo.InvariantCulture),
                "bytea" => value as byte[] ?? Encoding.UTF8.GetBytes(System.Convert.ToString(value, CultureInfo.InvariantCulture)!),
                "uuid" => value is byte[] bytes ? new Guid(bytes) : Guid.Parse(System.Convert.ToString(value, CultureInfo.InvariantCulture)!),
                _ => throw new NotSupportedException("desteklenmeyen PostgreSQL tipi " + column.DataType)
            };
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException or NotSupportedException)
        {
            throw new InvalidDataException($"{table}.{column.Name}: '{value}' değeri {column.DataType} tipine çevrilemedi ({exception.Message}).", exception);
        }
    }

    private static DateTime ParseTimestamp(string text)
    {
        text = text.Trim();
        if (OffsetSuffix().IsMatch(text))
        {
            DateTimeOffset withOffset = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None);
            return DateTime.SpecifyKind(withOffset.UtcDateTime, DateTimeKind.Unspecified);
        }

        return DateTime.SpecifyKind(DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Unspecified);
    }

    [GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetSuffix();

    private static NpgsqlDbType DbType(string dataType) => dataType switch
    {
        "boolean" => NpgsqlDbType.Boolean,
        "smallint" => NpgsqlDbType.Smallint,
        "integer" => NpgsqlDbType.Integer,
        "bigint" => NpgsqlDbType.Bigint,
        "numeric" => NpgsqlDbType.Numeric,
        "double precision" => NpgsqlDbType.Double,
        "real" => NpgsqlDbType.Real,
        "timestamp without time zone" => NpgsqlDbType.Timestamp,
        "timestamp with time zone" => NpgsqlDbType.TimestampTz,
        "date" => NpgsqlDbType.Date,
        "character varying" => NpgsqlDbType.Varchar,
        "character" => NpgsqlDbType.Char,
        "bytea" => NpgsqlDbType.Bytea,
        "uuid" => NpgsqlDbType.Uuid,
        _ => NpgsqlDbType.Text
    };

    // Sums of every money/quantity-like column: all numeric and floating columns plus integer columns that are not
    // keys or references. Exact for numeric/integer, 1e-6 relative tolerance for floating point.
    private static (string Text, bool Ok) CompareSums(PgTable table, SourceTable source, List<object?[]> target)
    {
        List<string> parts = [];
        bool ok = true;
        for (int i = 0; i < source.Columns.Count; i++)
        {
            PgColumn column = source.Columns[i];
            bool floating = column.DataType is "double precision" or "real";
            bool summed = column.DataType == "numeric" || floating
                || (column.DataType is "integer" or "bigint" or "smallint" && !table.PrimaryKey.Contains(column.Name) && !column.Name.EndsWith("id", StringComparison.Ordinal)
                    && !column.Name.EndsWith("revision", StringComparison.Ordinal) && !column.Name.EndsWith("version", StringComparison.Ordinal));
            if (!summed)
            {
                continue;
            }

            decimal sourceSum = source.Rows.Sum(row => row[i] is null ? 0m : System.Convert.ToDecimal(row[i], CultureInfo.InvariantCulture));
            decimal targetSum = target.Sum(row => row[i] is null ? 0m : System.Convert.ToDecimal(row[i], CultureInfo.InvariantCulture));
            bool equal = floating
                ? Math.Abs(sourceSum - targetSum) <= Math.Max(1e-6m, Math.Abs(sourceSum) * 1e-9m)
                : sourceSum == targetSum;
            ok &= equal;
            parts.Add($"{column.Name}={Normalize(sourceSum)}{(equal ? "" : "≠" + Normalize(targetSum))}");
        }

        return (parts.Count == 0 ? "-" : string.Join("; ", parts), ok);
    }

    private static string Hash(PgTable table, List<PgColumn> columns, List<object?[]> rows)
    {
        int[] keyOrdinals = table.PrimaryKey.Select(key => columns.FindIndex(column => column.Name == key)).Where(index => index >= 0).ToArray();
        IEnumerable<string> lines = rows.Select(row => string.Join("\u001f", row.Select(Normalize)));
        List<string> ordered = keyOrdinals.Length == 0
            ? lines.Order(StringComparer.Ordinal).ToList()
            : rows.OrderBy(row => string.Join("\u001f", keyOrdinals.Select(index => SortKey(row[index]))), StringComparer.Ordinal)
                .Select(row => string.Join("\u001f", row.Select(Normalize))).ToList();
        return System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001e", ordered))));
    }

    private static string SortKey(object? value) => value switch
    {
        null => "",
        short or int or long => System.Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString("D20", CultureInfo.InvariantCulture),
        _ => Normalize(value)
    };

    // PostgreSQL timestamps keep microseconds and Npgsql truncates the extra 100 ns digit, so do the same here.
    private static string Normalize(object? value) => value switch
    {
        null => "\u2205",
        bool flag => flag ? "1" : "0",
        decimal number => number.ToString("0.############################", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        DateTime time => new DateTime(time.Ticks / 10 * 10).ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture),
        byte[] bytes => System.Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static async Task<List<PgTable>> ReadPgTablesAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string schema)
    {
        Dictionary<string, PgTable> tables = new(StringComparer.Ordinal);
        await using (NpgsqlCommand command = new("""
            SELECT c.table_name, c.column_name, c.data_type, c.is_nullable = 'YES'
            FROM information_schema.columns c
            JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name AND t.table_type = 'BASE TABLE'
            WHERE c.table_schema = @schema
            ORDER BY c.table_name, c.ordinal_position;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("schema", schema);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string name = reader.GetString(0);
                if (SkippedTables.Contains(name))
                {
                    continue;
                }

                if (!tables.TryGetValue(name, out PgTable? table))
                {
                    table = new PgTable(name, [], [], new HashSet<string>(StringComparer.Ordinal));
                    tables[name] = table;
                }

                table.Columns.Add(new PgColumn(reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
            }
        }

        await using (NpgsqlCommand command = new("""
            SELECT rel.relname, att.attname
            FROM pg_index idx
            JOIN pg_class rel ON rel.oid = idx.indrelid
            JOIN pg_namespace ns ON ns.oid = rel.relnamespace
            JOIN LATERAL unnest(idx.indkey) WITH ORDINALITY AS k(attnum, ord) ON true
            JOIN pg_attribute att ON att.attrelid = rel.oid AND att.attnum = k.attnum
            WHERE idx.indisprimary AND ns.nspname = @schema
            ORDER BY rel.relname, k.ord;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("schema", schema);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (tables.TryGetValue(reader.GetString(0), out PgTable? table))
                {
                    table.PrimaryKey.Add(reader.GetString(1));
                }
            }
        }

        await using (NpgsqlCommand command = new("""
            SELECT src.relname, dst.relname
            FROM pg_constraint con
            JOIN pg_class src ON src.oid = con.conrelid
            JOIN pg_class dst ON dst.oid = con.confrelid
            JOIN pg_namespace ns ON ns.oid = src.relnamespace
            WHERE con.contype = 'f' AND ns.nspname = @schema;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("schema", schema);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (tables.TryGetValue(reader.GetString(0), out PgTable? table) && reader.GetString(1) != table.Name)
                {
                    table.References.Add(reader.GetString(1));
                }
            }
        }

        return tables.Values.ToList();
    }

    private static List<PgTable> TopologicalOrder(List<PgTable> tables)
    {
        Dictionary<string, PgTable> byName = tables.ToDictionary(table => table.Name, StringComparer.Ordinal);
        List<PgTable> ordered = [];
        HashSet<string> done = new(StringComparer.Ordinal);
        HashSet<string> visiting = new(StringComparer.Ordinal);

        void Visit(PgTable table)
        {
            if (done.Contains(table.Name))
            {
                return;
            }

            if (!visiting.Add(table.Name))
            {
                throw new InvalidOperationException("Döngüsel yabancı anahtar: " + table.Name);
            }

            foreach (string reference in table.References.Order(StringComparer.Ordinal))
            {
                if (byName.TryGetValue(reference, out PgTable? parent))
                {
                    Visit(parent);
                }
            }

            visiting.Remove(table.Name);
            done.Add(table.Name);
            ordered.Add(table);
        }

        foreach (PgTable table in tables.OrderBy(table => table.Name, StringComparer.Ordinal))
        {
            Visit(table);
        }

        return ordered;
    }

    private static async Task ResetIdentitiesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema)
    {
        List<(string Table, string Column, string Sequence)> sequences = [];
        await using (NpgsqlCommand command = new("""
            SELECT table_name, column_name, pg_get_serial_sequence(quote_ident(table_schema) || '.' || quote_ident(table_name), column_name)
            FROM information_schema.columns
            WHERE table_schema = @schema AND (is_identity = 'YES' OR column_default LIKE 'nextval(%');
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("schema", schema);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(2))
                {
                    sequences.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                }
            }
        }

        foreach ((string table, string column, string sequence) in sequences)
        {
            await using NpgsqlCommand command = new(
                $"SELECT setval(@sequence, COALESCE((SELECT MAX({column}) FROM {schema}.{table}), 0) + 1, false);", connection, transaction);
            command.Parameters.AddWithValue("sequence", sequence);
            await command.ExecuteScalarAsync();
        }

        Console.WriteLine($"{schema}: {sequences.Count} kimlik (identity) sırası en büyük Id'den sonraya ayarlandı.");
    }

    private static SqliteConnection OpenSnapshot(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static Dictionary<string, string> SqliteTables(SqliteConnection connection)
    {
        Dictionary<string, string> tables = new(StringComparer.Ordinal);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string name = reader.GetString(0);
            if (!SkippedTables.Contains(name))
            {
                tables[name.ToLowerInvariant()] = name;
            }
        }

        return tables;
    }

    private static long CountSqlite(SqliteConnection connection, string table, string? where = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"" + (where is null ? ";" : $" WHERE {where};");
        return System.Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        return System.Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }
}
