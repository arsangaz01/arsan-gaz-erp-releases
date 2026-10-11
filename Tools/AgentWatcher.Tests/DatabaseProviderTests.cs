using ArsanGazERP.Data;
using ArsanGazERP.Services;
using Npgsql;

namespace ArsanGazERP.AgentWatcher.Tests;

internal static class DatabaseProviderTests
{
    public static async Task RunAsync()
    {
        ParametersAreRewrittenForPostgres();
        NpgsqlParameterNamesDropSqlitePrefix();
        PostgresConnectionStringSelectsSchemaAndReadOnlyMode();
        await CampaignStoreRoundTripsOnPostgresWhenConfiguredAsync();
        Console.WriteLine("PASS: database provider SQL translation and connection settings.");
    }

    private static void ParametersAreRewrittenForPostgres()
    {
        string sql = "SELECT Id FROM Campaigns WHERE OwnerSid = $owner AND (CAST($state AS TEXT) IS NULL OR State = $state) AND json_extract(Data, '$.x') = $x2;";
        string rewritten = ArsanDatabase.ToPostgresSql(sql);
        Expect(rewritten == "SELECT Id FROM Campaigns WHERE OwnerSid = @owner AND (CAST(@state AS TEXT) IS NULL OR State = @state) AND json_extract(Data, '$.x') = @x2;",
            "ToPostgresSql must rewrite $name parameters and leave JSON paths alone: " + rewritten);
        Expect(ArsanDatabase.ToPostgresSql("SELECT @already, $$literal$$") == "SELECT @already, $$literal$$",
            "ToPostgresSql must not touch @ parameters or dollar quoting.");
    }

    private static void NpgsqlParameterNamesDropSqlitePrefix()
    {
        using NpgsqlCommand command = new();
        ArsanDatabase.AddParameter(command, "$owner", "S-1");
        ArsanDatabase.AddParameter(command, "@state", null);
        Expect(command.Parameters[0].ParameterName == "owner", "Npgsql parameter must not keep the $ prefix.");
        Expect(command.Parameters[1].ParameterName == "state" && command.Parameters[1].Value == DBNull.Value,
            "Null parameters must be sent as DBNull.");
    }

    private static void PostgresConnectionStringSelectsSchemaAndReadOnlyMode()
    {
        const string baseConnection = "Host=127.0.0.1;Database=arsangaz;Username=u;Password=p";
        NpgsqlConnectionStringBuilder crm = new(ArsanDatabase.PostgresConnectionString(baseConnection, DatabaseKind.Crm, readOnly: true));
        Expect(crm.SearchPath == "crm", "CRM connection must use the crm schema.");
        Expect(crm.Options?.Contains("default_transaction_read_only=on", StringComparison.Ordinal) == true,
            "Read-only connections must enforce read-only transactions on the server.");
        NpgsqlConnectionStringBuilder erp = new(ArsanDatabase.PostgresConnectionString(baseConnection, DatabaseKind.Erp));
        Expect(erp.SearchPath == "erp" && string.IsNullOrEmpty(erp.Options), "ERP write connection must use the erp schema without read-only options.");
    }

    // Opt-in: runs only when ARSANGAZ_TEST_PG_CONNECTION points at a disposable PostgreSQL database.
    private static async Task CampaignStoreRoundTripsOnPostgresWhenConfiguredAsync()
    {
        string? connection = Environment.GetEnvironmentVariable("ARSANGAZ_TEST_PG_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.WriteLine("SKIP: PostgreSQL CampaignStore test (set ARSANGAZ_TEST_PG_CONNECTION).");
            return;
        }

        Environment.SetEnvironmentVariable("ARSANGAZ_DB_PROVIDER", "postgres");
        Environment.SetEnvironmentVariable("ARSANGAZ_PG_CONNECTION", connection);
        Expect(ArsanDatabase.IsPostgres, "ARSANGAZ_TEST_PG_CONNECTION must be set before the provider is first read (use --database-provider-only).");

        string attachments = Path.Combine(Path.GetTempPath(), "arsangaz-pg-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            CampaignStore store = CampaignStore.CreateForPostgres(attachments, "S-1-5-TEST-PG", () => DateTimeOffset.UtcNow);
            await store.InitializeAsync();
            Expect(store.UsesPostgres, "CreateForPostgres must use PostgreSQL.");
        }
        finally
        {
            if (Directory.Exists(attachments))
            {
                Directory.Delete(attachments, recursive: true);
            }
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
