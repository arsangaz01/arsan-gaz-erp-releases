using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArsanGazERP.Data;

// EF Core side of ArsanDatabase: provider selection for the DbContexts and the PostgreSQL schema.
// SQLite keeps its migrations and schema services; on PostgreSQL every table is created from the current EF model
// in its own schema (erp / crm) with lower-case identifiers so the raw SQL works unquoted on both engines.
public static class ArsanDbEf
{
    public static void Configure(DbContextOptionsBuilder options, DatabaseKind kind, string sqliteConnectionString)
    {
        if (ArsanDatabase.IsPostgres)
        {
            options.UseNpgsql(ArsanDatabase.PostgresConnectionString(kind),
                npgsql => npgsql.MigrationsHistoryTable("__efmigrationshistory", ArsanDatabase.Schema(kind)));
        }
        else
        {
            options.UseSqlite(sqliteConnectionString);
        }
    }

    public static void ApplyPostgresConventions(ModelBuilder modelBuilder, DatabaseKind kind)
    {
        if (!ArsanDatabase.IsPostgres)
        {
            return;
        }

        modelBuilder.HasDefaultSchema(ArsanDatabase.Schema(kind));
        foreach (IMutableEntityType entity in modelBuilder.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is { } table)
            {
                entity.SetTableName(table.ToLowerInvariant());
            }

            StoreObjectIdentifier store = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            foreach (IMutableProperty property in entity.GetProperties())
            {
                property.SetColumnName(property.GetColumnName(store)?.ToLowerInvariant() ?? property.Name.ToLowerInvariant());
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                {
                    property.SetColumnType("timestamp without time zone");
                }
            }

            foreach (IMutableKey key in entity.GetKeys())
            {
                key.SetName(key.GetName()!.ToLowerInvariant());
            }

            foreach (IMutableForeignKey foreignKey in entity.GetForeignKeys())
            {
                foreignKey.SetConstraintName(foreignKey.GetConstraintName()!.ToLowerInvariant());
            }

            foreach (IMutableIndex index in entity.GetIndexes())
            {
                index.SetDatabaseName(index.GetDatabaseName()!.ToLowerInvariant());
            }
        }
    }

    // PostgreSQL counterpart of Database.Migrate / EnsureCreated: creates the schema and, if it has no tables yet,
    // all tables of this context. EnsureCreated cannot be used because it looks at every schema of the database.
    public static async Task EnsurePostgresSchemaAsync(DbContext db, DatabaseKind kind, CancellationToken cancellationToken = default)
    {
        if (!ArsanDatabase.IsPostgres)
        {
            return;
        }

        string schema = ArsanDatabase.Schema(kind);
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            DbConnection connection = db.Database.GetDbConnection();
            await using (DbCommand create = ArsanDatabase.Command(connection, null, $"CREATE SCHEMA IF NOT EXISTS {schema};"))
            {
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            long tables;
            await using (DbCommand count = ArsanDatabase.Command(connection, null,
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = $schema AND table_type = 'BASE TABLE';", ("$schema", schema)))
            {
                tables = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
            }

            if (tables == 0)
            {
                await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(cancellationToken);
            }

            string extraSql = kind == DatabaseKind.Erp ? PostgresSchema.Erp : PostgresSchema.Crm;
            if (extraSql.Length > 0)
            {
                await using DbCommand extra = ArsanDatabase.Command(connection, null, extraSql);
                await extra.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
