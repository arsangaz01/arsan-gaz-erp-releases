using System.Data.Common;
using ArsanTupTakip.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ArsanTupTakip.Database;

// PostgreSQL only. The connection comes from TUPTAKIP_PG_CONNECTION (systemd EnvironmentFile on the server); all
// tables live in the "tuptakip" schema with lower-case identifiers so they can be inspected with psql unquoted.
public static class TupTakipDatabase
{
    public const string Schema = "tuptakip";
    public const string ConnectionVariable = "TUPTAKIP_PG_CONNECTION";

    // Columns added after the first release go here as idempotent SQL (CREATE ... IF NOT EXISTS / ALTER ... ADD COLUMN
    // IF NOT EXISTS); CreateTablesAsync only runs on an empty schema.
    internal const string UpgradeSql = """
        CREATE TABLE IF NOT EXISTS tuptakip.user_permission_overrides (
            user_id integer NOT NULL,
            permission varchar(60) NOT NULL,
            granted boolean NOT NULL,
            updated_at timestamp without time zone NOT NULL DEFAULT now(),
            updated_by varchar(60) NOT NULL DEFAULT '',
            CONSTRAINT pk_user_permission_overrides PRIMARY KEY (user_id, permission)
        );
        CREATE TABLE IF NOT EXISTS tuptakip.request_keys (
            user_id integer NOT NULL,
            request_key varchar(200) NOT NULL,
            created_at timestamp without time zone NOT NULL DEFAULT now(),
            status_code integer NULL,
            content_type varchar(100) NULL,
            response text NULL,
            CONSTRAINT pk_request_keys PRIMARY KEY (user_id, request_key)
        );
        DO $$
        DECLARE item record;
        BEGIN
            FOR item IN SELECT * FROM (VALUES
                ('ix_cylindertripvehicles_tripid_plate', 'cylindertripvehicles', 'tripid, plate'),
                ('ix_cylindertripcylinders_tripid_cylinderid', 'cylindertripcylinders', 'tripid, cylinderid')) AS v(name, tbl, cols)
            LOOP
                IF EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'tuptakip' AND indexname = item.name
                           AND indexdef NOT LIKE 'CREATE UNIQUE%') THEN
                    BEGIN
                        EXECUTE format('CREATE UNIQUE INDEX %I ON tuptakip.%I (%s)', item.name || '_u', item.tbl, item.cols);
                        EXECUTE format('DROP INDEX tuptakip.%I', item.name);
                        EXECUTE format('ALTER INDEX tuptakip.%I RENAME TO %I', item.name || '_u', item.name);
                    EXCEPTION WHEN unique_violation THEN
                        RAISE WARNING 'ArsanTupTakip: % tekil yapılamadı (yinelenen kayıt var)', item.name;
                    END;
                END IF;
            END LOOP;
        END $$;
        """;

    static TupTakipDatabase()
    {
        // DateTime values are stored as wall-clock "timestamp without time zone" (server time zone Europe/Istanbul).
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }

    public static string ConnectionString
    {
        get
        {
            string raw = Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"{ConnectionVariable} ortam değişkeni tanımlı değil.");
            NpgsqlConnectionStringBuilder builder = new(raw) { SearchPath = Schema };
            if (string.IsNullOrEmpty(builder.ApplicationName))
            {
                builder.ApplicationName = "ArsanTupTakip";
            }

            return builder.ToString();
        }
    }

    public static void Initialize()
    {
        // Touching the type runs the static constructor before the first DbContext is created.
    }

    public static async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using TupTakipDbContext db = new();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            DbConnection connection = db.Database.GetDbConnection();
            await ExecuteAsync(connection, $"CREATE SCHEMA IF NOT EXISTS {Schema};", cancellationToken);
            long tables;
            await using (DbCommand count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @schema AND table_type = 'BASE TABLE';";
                DbParameter parameter = count.CreateParameter();
                parameter.ParameterName = "schema";
                parameter.Value = Schema;
                count.Parameters.Add(parameter);
                tables = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
            }

            if (tables == 0)
            {
                await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(cancellationToken);
            }

            if (UpgradeSql.Length > 0)
            {
                await ExecuteAsync(connection, UpgradeSql, cancellationToken);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class TupTakipDbContext : DbContext
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Cylinder> Cylinders => Set<Cylinder>();
    public DbSet<CylinderMovement> CylinderMovements => Set<CylinderMovement>();
    public DbSet<CylinderAudit> CylinderAudits => Set<CylinderAudit>();
    public DbSet<CylinderTrip> CylinderTrips => Set<CylinderTrip>();
    public DbSet<CylinderTripVehicle> CylinderTripVehicles => Set<CylinderTripVehicle>();
    public DbSet<CylinderTripCylinder> CylinderTripCylinders => Set<CylinderTripCylinder>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        TupTakipDatabase.Initialize();
        options.UseNpgsql(TupTakipDatabase.ConnectionString,
            npgsql => npgsql.MigrationsHistoryTable("__efmigrationshistory", TupTakipDatabase.Schema));
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(TupTakipDatabase.Schema);
        b.Entity<Customer>().HasIndex(x => x.Code).IsUnique();
        b.Entity<Cylinder>().HasIndex(x => x.SerialNumber).IsUnique();
        b.Entity<Cylinder>().HasIndex(x => x.Barcode).IsUnique();
        b.Entity<Cylinder>().HasIndex(x => x.Status);
        b.Entity<Cylinder>().Property(x => x.Capacity).HasPrecision(18, 4);
        b.Entity<Cylinder>().HasOne(x => x.CurrentCustomer).WithMany(x => x.Cylinders).HasForeignKey(x => x.CurrentCustomerId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<CylinderMovement>().HasOne(x => x.Cylinder).WithMany(x => x.Movements).HasForeignKey(x => x.CylinderId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CylinderMovement>().HasIndex(x => x.MovementDate);
        b.Entity<CylinderAudit>().HasIndex(x => new { x.CustomerId, x.AuditedAt });
        b.Entity<CylinderTrip>().HasIndex(x => x.Status);
        b.Entity<Vehicle>().HasIndex(x => x.Plate).IsUnique();
        b.Entity<CylinderTripVehicle>().HasIndex(x => new { x.TripId, x.Plate }).IsUnique();
        b.Entity<CylinderTripCylinder>().HasIndex(x => new { x.TripId, x.CylinderId }).IsUnique();
        // Optimistic concurrency: a save fails with DbUpdateConcurrencyException when another user changed the row.
        b.Entity<Cylinder>().Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        b.Entity<Customer>().Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        b.Entity<Vehicle>().Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        b.Entity<CylinderTrip>().Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        b.Entity<CylinderTripCylinder>().HasOne(x => x.Cylinder).WithMany().HasForeignKey(x => x.CylinderId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CylinderTripVehicle>().HasOne(x => x.Trip).WithMany(x => x.Vehicles).HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CylinderTripCylinder>().HasOne(x => x.Trip).WithMany(x => x.Cylinders).HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
        ApplyLowerCaseNames(b);
    }

    private static void ApplyLowerCaseNames(ModelBuilder modelBuilder)
    {
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
}
