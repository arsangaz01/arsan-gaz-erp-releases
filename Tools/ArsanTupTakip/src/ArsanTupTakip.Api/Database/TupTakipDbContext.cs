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
    internal const string UpgradeSql = "";

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
        b.Entity<CylinderTripVehicle>().HasIndex(x => new { x.TripId, x.Plate });
        b.Entity<CylinderTripCylinder>().HasIndex(x => new { x.TripId, x.CylinderId });
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
