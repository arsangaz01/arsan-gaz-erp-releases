using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

// The ERP and the mobile server both open this database; whichever starts first adds the cylinder tracking columns.
public static class CylinderSchemaService
{
    private const string AuditTableSql = """
CREATE TABLE IF NOT EXISTS CylinderAudits (
 Id INTEGER NOT NULL CONSTRAINT PK_CylinderAudits PRIMARY KEY AUTOINCREMENT,
 CustomerId INTEGER NOT NULL,
 CustomerName TEXT NOT NULL,
 AuditedAt TEXT NOT NULL,
 ExpectedCount INTEGER NOT NULL DEFAULT 0,
 FoundCount INTEGER NOT NULL DEFAULT 0,
 MissingSerials TEXT NOT NULL DEFAULT '',
 UnexpectedSerials TEXT NOT NULL DEFAULT '',
 UnknownCodes TEXT NOT NULL DEFAULT '',
 Note TEXT NULL,
 PerformedBy TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_CylinderAudits_CustomerId_AuditedAt ON CylinderAudits(CustomerId, AuditedAt);
CREATE INDEX IF NOT EXISTS IX_Cylinders_Status ON Cylinders(Status);
CREATE TABLE IF NOT EXISTS CylinderTrips (
 Id INTEGER NOT NULL CONSTRAINT PK_CylinderTrips PRIMARY KEY AUTOINCREMENT,
 Title TEXT NOT NULL DEFAULT '',
 Status INTEGER NOT NULL DEFAULT 1,
 StartedAt TEXT NOT NULL,
 ClosedAt TEXT NULL,
 Note TEXT NULL,
 CreatedBy TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_CylinderTrips_Status ON CylinderTrips(Status);
CREATE TABLE IF NOT EXISTS CylinderTripVehicles (
 Id INTEGER NOT NULL CONSTRAINT PK_CylinderTripVehicles PRIMARY KEY AUTOINCREMENT,
 TripId INTEGER NOT NULL,
 Plate TEXT NOT NULL,
 AddedAt TEXT NOT NULL,
 ReturnedAt TEXT NULL,
 CONSTRAINT FK_CylinderTripVehicles_Trip FOREIGN KEY (TripId) REFERENCES CylinderTrips(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_CylinderTripVehicles_TripId_Plate ON CylinderTripVehicles(TripId, Plate);
CREATE TABLE IF NOT EXISTS CylinderTripCylinders (
 Id INTEGER NOT NULL CONSTRAINT PK_CylinderTripCylinders PRIMARY KEY AUTOINCREMENT,
 TripId INTEGER NOT NULL,
 CylinderId INTEGER NOT NULL,
 VehiclePlate TEXT NOT NULL,
 SerialNumber TEXT NOT NULL,
 GasType TEXT NOT NULL DEFAULT '',
 LoadedAt TEXT NOT NULL,
 DeliveredAt TEXT NULL,
 ReturnedAt TEXT NULL,
 CONSTRAINT FK_CylinderTripCylinders_Trip FOREIGN KEY (TripId) REFERENCES CylinderTrips(Id) ON DELETE CASCADE,
 CONSTRAINT FK_CylinderTripCylinders_Cylinder FOREIGN KEY (CylinderId) REFERENCES Cylinders(Id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS IX_CylinderTripCylinders_TripId_CylinderId ON CylinderTripCylinders(TripId, CylinderId);
""";

    public static async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        await using ArsanGazDbContext db = new();
        if (ArsanDatabase.IsPostgres)
        {
            // PostgreSQL tables come from the current EF model, which already has these columns and CylinderAudits.
            await ArsanDbEf.EnsurePostgresSchemaAsync(db, DatabaseKind.Erp, cancellationToken);
            return;
        }

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            HashSet<string> cylinderColumns = await ColumnsAsync(connection, "Cylinders", cancellationToken);
            if (cylinderColumns.Count == 0)
            {
                return;
            }

            await AddColumnAsync(connection, cylinderColumns, "Cylinders", "IsFull", "INTEGER NULL", cancellationToken);
            await AddColumnAsync(connection, cylinderColumns, "Cylinders", "VehiclePlate", "TEXT NULL", cancellationToken);

            HashSet<string> movementColumns = await ColumnsAsync(connection, "CylinderMovements", cancellationToken);
            await AddColumnAsync(connection, movementColumns, "CylinderMovements", "VehiclePlate", "TEXT NULL", cancellationToken);
            await AddColumnAsync(connection, movementColumns, "CylinderMovements", "IsFull", "INTEGER NULL", cancellationToken);

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = AuditTableSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static async Task<HashSet<string>> ColumnsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}');";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task AddColumnAsync(
        SqliteConnection connection,
        HashSet<string> existing,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        if (existing.Contains(column))
        {
            return;
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
        }
    }
}
