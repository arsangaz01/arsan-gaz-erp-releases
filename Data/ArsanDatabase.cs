using System.Data.Common;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace ArsanGazERP.Data;

public enum DatabaseProvider { Sqlite, Postgres }

public enum DatabaseKind { Erp, Crm }

// Chooses the database engine for the ERP and CRM data. SQLite (local files) is the default and stays in use until
// PostgreSQL is enabled explicitly: on the server with ARSANGAZ_DB_PROVIDER=postgres + ARSANGAZ_PG_CONNECTION, on the
// desktop with provider "postgres" in %LOCALAPPDATA%\ArsanGazERP\db-connection.json (connection DPAPI-protected).
// A PostgreSQL selection without a usable connection fails loudly; it never falls back to the local files.
public static partial class ArsanDatabase
{
    public const string ConfigFileName = "db-connection.json";
    private const long WriteLockKey = 0x4152534E47415A; // pg_advisory_xact_lock key shared by all ERP writers
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ArsanGazERP.db-connection.v1");
    private static readonly Lazy<Settings> Current = new(Load);

    static ArsanDatabase()
    {
        // DateTime values are stored as wall-clock "timestamp without time zone", exactly like the SQLite text dates.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }

    public static DatabaseProvider Provider => Current.Value.Provider;

    public static bool IsPostgres => Provider == DatabaseProvider.Postgres;

    public static string ConfigPath =>
        Environment.GetEnvironmentVariable("ARSANGAZ_DB_CONFIG") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", ConfigFileName);

    public static string SqlitePath(DatabaseKind kind) => kind == DatabaseKind.Erp
        ? Environment.GetEnvironmentVariable("ARSANGAZ_ERP_DB") is { Length: > 0 } erp
            ? erp
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "Data", "arsangaz-erp-v3.db")
        : Environment.GetEnvironmentVariable("ARSANGAZ_CRM_DB") is { Length: > 0 } crm
            ? crm
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "ArsanGazERP", "CRM", "arsangaz-crm.db");

    public static string Schema(DatabaseKind kind) => kind == DatabaseKind.Erp ? "erp" : "crm";

    public static string PostgresConnectionString(DatabaseKind kind, bool readOnly = false) =>
        PostgresConnectionString(RequirePostgresConnection(), kind, readOnly);

    public static string PostgresConnectionString(string baseConnection, DatabaseKind kind, bool readOnly = false)
    {
        NpgsqlConnectionStringBuilder builder = new(baseConnection) { SearchPath = Schema(kind) };
        if (string.IsNullOrEmpty(builder.ApplicationName))
        {
            builder.ApplicationName = "ArsanGazERP";
        }

        if (readOnly)
        {
            builder.Options = "-c default_transaction_read_only=on";
        }

        return builder.ToString();
    }

    // Human-readable location without secrets (for messages and health checks).
    public static string Describe(DatabaseKind kind)
    {
        if (!IsPostgres)
        {
            return SqlitePath(kind);
        }

        NpgsqlConnectionStringBuilder builder = new(RequirePostgresConnection());
        return $"PostgreSQL {builder.Host}:{builder.Port}/{builder.Database} ({Schema(kind)})";
    }

    public static bool Exists(DatabaseKind kind) => IsPostgres || File.Exists(SqlitePath(kind));

    public static DbConnection CreateConnection(DatabaseKind kind, bool readOnly = false, SqliteOptions? sqlite = null)
    {
        if (IsPostgres)
        {
            return new NpgsqlConnection(PostgresConnectionString(kind, readOnly));
        }

        SqliteOptions options = sqlite ?? SqliteOptions.Default;
        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.PathOverride ?? SqlitePath(kind),
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Cache = options.SharedCache ? SqliteCacheMode.Shared : SqliteCacheMode.Private,
            DefaultTimeout = options.TimeoutSeconds,
            ForeignKeys = options.ForeignKeys ? true : null
        }.ToString());
    }

    public static async Task<DbConnection> OpenAsync(DatabaseKind kind, bool readOnly = false, CancellationToken cancellationToken = default, SqliteOptions? sqlite = null)
    {
        DbConnection connection = CreateConnection(kind, readOnly, sqlite);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public static DbConnection Open(DatabaseKind kind, bool readOnly = false, SqliteOptions? sqlite = null)
    {
        DbConnection connection = CreateConnection(kind, readOnly, sqlite);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // SQL in this code base is written for SQLite with $name parameters and unquoted identifiers. PostgreSQL folds
    // unquoted identifiers to lower case and the EF model uses lower-case names there, so only the parameter
    // prefix needs translating.
    public static string Sql(string sql) => IsPostgres ? ToPostgresSql(sql) : sql;

    public static string ToPostgresSql(string sql) => ParameterPattern().Replace(sql, "@$1");

    // Opens the PostgreSQL database even when SQLite is the configured provider (migration tool, tests, harness).
    public static async Task<DbConnection> OpenPostgresAsync(DatabaseKind kind, bool readOnly = false, CancellationToken cancellationToken = default)
    {
        NpgsqlConnection connection = new(PostgresConnectionString(kind, readOnly));
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql(sql);
        foreach ((string name, object? value) in parameters)
        {
            AddParameter(command, name, value);
        }

        return command;
    }

    public static void AddParameter(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = command is NpgsqlCommand ? name.TrimStart('$', '@', ':') : name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    // Case-insensitive LIKE: SQLite LIKE already ignores ASCII case; PostgreSQL needs ILIKE.
    public static string Like => IsPostgres ? "ILIKE" : "LIKE";

    // Boolean EF columns are INTEGER 0/1 in SQLite and boolean in PostgreSQL.
    public static object Flag(bool value) => IsPostgres ? value : value ? 1 : 0;

    public static bool ReadFlag(DbDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && Convert.ToBoolean(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    // Value for an EF DateTime column written from raw SQL: SQLite keeps the existing ISO text, PostgreSQL gets a UTC
    // wall-clock timestamp.
    public static object Timestamp(DateTimeOffset value) => IsPostgres
        ? value.UtcDateTime
        : value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    public static DateTimeOffset? ReadUtc(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        object value = reader.GetValue(ordinal);
        if (value is DateTime dateTime)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc));
        }

        return DateTimeOffset.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    public static bool IsUniqueViolation(Exception exception) =>
        exception is SqliteException { SqliteErrorCode: 19 } || exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    // SQLite BEGIN IMMEDIATE serializes writers for the whole file; on PostgreSQL the same guarantee comes from a
    // transaction-scoped advisory lock taken by every caller of this method.
    public static async Task<DbTransaction> BeginWriteTransactionAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        if (connection is SqliteConnection sqlite)
        {
            return sqlite.BeginTransaction(deferred: false);
        }

        DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using DbCommand command = Command(connection, transaction, $"SET LOCAL lock_timeout = '30s'; SELECT pg_advisory_xact_lock({WriteLockKey});");
        await command.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    public static string? PostgresConnectionOrNull => Current.Value.PostgresConnection;

    private static string RequirePostgresConnection() =>
        Current.Value.PostgresConnection ?? throw new InvalidOperationException(
            "PostgreSQL seçili ama bağlantı bilgisi yok. ARSANGAZ_PG_CONNECTION ortam değişkenini veya " + ConfigPath + " dosyasını kontrol edin.");

    private static Settings Load()
    {
        DatabaseProvider? provider = ParseProvider(Environment.GetEnvironmentVariable("ARSANGAZ_DB_PROVIDER"));
        string? connection = Environment.GetEnvironmentVariable("ARSANGAZ_PG_CONNECTION") is { Length: > 0 } fromEnvironment ? fromEnvironment : null;
        string path = ConfigPath;
        if (File.Exists(path))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (provider is null && root.TryGetProperty("provider", out JsonElement providerValue))
            {
                provider = ParseProvider(providerValue.GetString())
                    ?? throw new InvalidOperationException($"{path}: geçersiz provider '{providerValue.GetString()}' (sqlite veya postgres olmalı).");
            }

            // The protected connection is only decrypted when PostgreSQL is selected, so a SQLite installation keeps
            // starting even if the blob cannot be decrypted (another Windows user, reinstalled profile).
            if (provider == DatabaseProvider.Postgres && connection is null)
            {
                if (root.TryGetProperty("postgresProtected", out JsonElement protectedValue) && protectedValue.GetString() is { Length: > 0 } blob)
                {
                    connection = Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(blob)));
                }
                else if (root.TryGetProperty("postgresConnection", out JsonElement plainValue) && plainValue.GetString() is { Length: > 0 } plain)
                {
                    connection = plain;
                }
            }
        }

        return new Settings(provider ?? DatabaseProvider.Sqlite, connection);
    }

    public static byte[] Protect(byte[] clearBytes) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Protect(clearBytes, Entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException("DPAPI yalnızca Windows'ta vardır; sunucuda ARSANGAZ_PG_CONNECTION kullanın.");

    private static byte[] Unprotect(byte[] blob) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException("DPAPI ile korunan bağlantı bu sunucuda açılamaz; ARSANGAZ_PG_CONNECTION kullanın.");

    private static DatabaseProvider? ParseProvider(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "sqlite" => DatabaseProvider.Sqlite,
        "postgres" or "postgresql" or "pg" or "npgsql" => DatabaseProvider.Postgres,
        _ => throw new InvalidOperationException($"Geçersiz veritabanı sağlayıcısı: '{value}' (sqlite veya postgres olmalı).")
    };

    [GeneratedRegex(@"(?<![\w$@])\$([A-Za-z_]\w*)")]
    private static partial Regex ParameterPattern();

    private sealed record Settings(DatabaseProvider Provider, string? PostgresConnection);
}

// SQLite-only connection settings; ignored on PostgreSQL. PathOverride lets callers (tests, CampaignStore) use an
// explicit file instead of the configured ERP/CRM path.
public sealed record SqliteOptions(int TimeoutSeconds = 30, bool SharedCache = false, bool ForeignKeys = false, string? PathOverride = null)
{
    public static readonly SqliteOptions Default = new();
}
