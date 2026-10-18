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
CREATE TABLE IF NOT EXISTS Vehicles (
 Id INTEGER NOT NULL CONSTRAINT PK_Vehicles PRIMARY KEY AUTOINCREMENT,
 Plate TEXT NOT NULL,
 Name TEXT NULL,
 VehicleType TEXT NULL,
 DriverName TEXT NULL,
 CylinderCapacity INTEGER NULL,
 Note TEXT NULL,
 IsActive INTEGER NOT NULL DEFAULT 1,
 CreatedAt TEXT NOT NULL,
 UpdatedAt TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_Vehicles_Plate ON Vehicles(Plate);
INSERT OR IGNORE INTO Vehicles (Plate, IsActive, CreatedAt)
SELECT DISTINCT p.Plate, 1, datetime('now', 'localtime') FROM (
 SELECT TRIM(VehiclePlate) AS Plate FROM Cylinders WHERE VehiclePlate IS NOT NULL
 UNION SELECT TRIM(Plate) FROM CylinderTripVehicles
 UNION SELECT TRIM(VehiclePlate) FROM CylinderTripCylinders
) p WHERE p.Plate <> '';
""";

    // Depot counts, label products, customer requests, customer locations, trip plans and trip route stops.
    private const string FieldWorkTablesSql = """
CREATE TABLE IF NOT EXISTS StockCounts (
 Id INTEGER NOT NULL CONSTRAINT PK_StockCounts PRIMARY KEY AUTOINCREMENT,
 Location TEXT NOT NULL,
 StartedAt TEXT NOT NULL,
 StartedBy TEXT NOT NULL,
 ClosedAt TEXT NULL,
 ClosedBy TEXT NULL,
 Note TEXT NULL,
 ExpectedCount INTEGER NOT NULL DEFAULT 0,
 MatchedCount INTEGER NOT NULL DEFAULT 0,
 MissingCount INTEGER NOT NULL DEFAULT 0,
 ExtraCount INTEGER NOT NULL DEFAULT 0,
 UnknownCount INTEGER NOT NULL DEFAULT 0,
 ExpectedSnapshot TEXT NULL
);
CREATE TABLE IF NOT EXISTS StockCountItems (
 Id INTEGER NOT NULL CONSTRAINT PK_StockCountItems PRIMARY KEY AUTOINCREMENT,
 CountId INTEGER NOT NULL,
 CylinderId INTEGER NULL,
 Code TEXT NOT NULL,
 ScannedAt TEXT NOT NULL,
 ScannedBy TEXT NOT NULL,
 StatusAtScan INTEGER NULL,
 PlaceAtScan TEXT NULL,
 CONSTRAINT FK_StockCountItems_StockCounts_CountId FOREIGN KEY (CountId) REFERENCES StockCounts (Id) ON DELETE CASCADE,
 CONSTRAINT FK_StockCountItems_Cylinders_CylinderId FOREIGN KEY (CylinderId) REFERENCES Cylinders (Id) ON DELETE SET NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_StockCountItems_CountId_CylinderId ON StockCountItems(CountId, CylinderId);
CREATE UNIQUE INDEX IF NOT EXISTS IX_StockCountItems_CountId_Code ON StockCountItems(CountId, Code);
CREATE INDEX IF NOT EXISTS IX_StockCounts_StartedAt ON StockCounts(StartedAt);
CREATE UNIQUE INDEX IF NOT EXISTS IX_StockCounts_Open_Location ON StockCounts(Location) WHERE ClosedAt IS NULL;
CREATE TABLE IF NOT EXISTS LabelTemplates (
 Id INTEGER NOT NULL CONSTRAINT PK_LabelTemplates PRIMARY KEY AUTOINCREMENT,
 Name TEXT NOT NULL,
 GasText TEXT NOT NULL DEFAULT '',
 Prefix TEXT NOT NULL DEFAULT '',
 Digits INTEGER NOT NULL DEFAULT 6,
 SampleCode TEXT NULL,
 TestText TEXT NULL,
 Title TEXT NOT NULL DEFAULT '',
 WidthMm INTEGER NOT NULL DEFAULT 50,
 HeightMm INTEGER NOT NULL DEFAULT 30,
 Copies INTEGER NOT NULL DEFAULT 1,
 ShowGas INTEGER NOT NULL DEFAULT 1,
 ShowSerial INTEGER NOT NULL DEFAULT 1,
 ShowTest INTEGER NOT NULL DEFAULT 1,
 ShowText INTEGER NOT NULL DEFAULT 1,
 CreatedAt TEXT NOT NULL,
 CreatedBy TEXT NOT NULL DEFAULT '',
 UpdatedAt TEXT NOT NULL,
 UpdatedBy TEXT NOT NULL DEFAULT ''
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_LabelTemplates_Name ON LabelTemplates(Name);
CREATE TABLE IF NOT EXISTS CustomerRequests (
 Id INTEGER NOT NULL CONSTRAINT PK_CustomerRequests PRIMARY KEY AUTOINCREMENT,
 CustomerId INTEGER NOT NULL,
 Kind TEXT NOT NULL DEFAULT '',
 Quantity INTEGER NULL,
 Note TEXT NULL,
 CreatedAt TEXT NOT NULL,
 CreatedBy TEXT NOT NULL DEFAULT '',
 Status TEXT NOT NULL DEFAULT 'acik',
 HandledAt TEXT NULL,
 HandledBy TEXT NULL,
 HandledNote TEXT NULL,
 CONSTRAINT FK_CustomerRequests_Customers_CustomerId FOREIGN KEY (CustomerId) REFERENCES Customers (Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_CustomerRequests_Status_CreatedAt ON CustomerRequests(Status, CreatedAt);
CREATE INDEX IF NOT EXISTS IX_CustomerRequests_CustomerId ON CustomerRequests(CustomerId);
CREATE TABLE IF NOT EXISTS CylinderTripStops (
 Id INTEGER NOT NULL CONSTRAINT PK_CylinderTripStops PRIMARY KEY AUTOINCREMENT,
 TripId INTEGER NOT NULL,
 Plate TEXT NOT NULL DEFAULT '',
 CustomerId INTEGER NOT NULL,
 Position INTEGER NOT NULL DEFAULT 0,
 AddedAt TEXT NOT NULL,
 AddedBy TEXT NOT NULL DEFAULT '',
 DoneAt TEXT NULL,
 Note TEXT NULL,
 AlertMeters INTEGER NULL,
 CONSTRAINT FK_CylinderTripStops_CylinderTrips_TripId FOREIGN KEY (TripId) REFERENCES CylinderTrips (Id) ON DELETE CASCADE,
 CONSTRAINT FK_CylinderTripStops_Customers_CustomerId FOREIGN KEY (CustomerId) REFERENCES Customers (Id) ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_CylinderTripStops_TripId_Plate_CustomerId ON CylinderTripStops(TripId, Plate, CustomerId);
CREATE TABLE IF NOT EXISTS CustomerLocations (
 CustomerId INTEGER NOT NULL CONSTRAINT PK_CustomerLocations PRIMARY KEY,
 Latitude REAL NOT NULL,
 Longitude REAL NOT NULL,
 UpdatedAt TEXT NOT NULL,
 UpdatedBy TEXT NOT NULL DEFAULT '',
 Source TEXT NOT NULL DEFAULT '',
 Verified INTEGER NOT NULL DEFAULT 1,
 Label TEXT NULL,
 CONSTRAINT FK_CustomerLocations_Customers_CustomerId FOREIGN KEY (CustomerId) REFERENCES Customers (Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS TripPlans (
 Id INTEGER NOT NULL CONSTRAINT PK_TripPlans PRIMARY KEY AUTOINCREMENT,
 PlanDate TEXT NOT NULL,
 Plate TEXT NOT NULL DEFAULT '',
 AlertMeters INTEGER NOT NULL DEFAULT 300,
 Note TEXT NULL,
 CreatedAt TEXT NOT NULL,
 CreatedBy TEXT NOT NULL DEFAULT '',
 UpdatedAt TEXT NOT NULL,
 UpdatedBy TEXT NOT NULL DEFAULT '',
 TripId INTEGER NULL,
 AppliedAt TEXT NULL,
 CONSTRAINT FK_TripPlans_CylinderTrips_TripId FOREIGN KEY (TripId) REFERENCES CylinderTrips (Id) ON DELETE SET NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_TripPlans_PlanDate_Plate ON TripPlans(PlanDate, Plate);
CREATE TABLE IF NOT EXISTS TripPlanStops (
 Id INTEGER NOT NULL CONSTRAINT PK_TripPlanStops PRIMARY KEY AUTOINCREMENT,
 PlanId INTEGER NOT NULL,
 CustomerId INTEGER NOT NULL,
 Position INTEGER NOT NULL DEFAULT 0,
 Note TEXT NULL,
 CONSTRAINT FK_TripPlanStops_TripPlans_PlanId FOREIGN KEY (PlanId) REFERENCES TripPlans (Id) ON DELETE CASCADE,
 CONSTRAINT FK_TripPlanStops_Customers_CustomerId FOREIGN KEY (CustomerId) REFERENCES Customers (Id) ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_TripPlanStops_PlanId_CustomerId ON TripPlanStops(PlanId, CustomerId);
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
            await AddColumnAsync(connection, cylinderColumns, "Cylinders", "OwnerCustomerId", "INTEGER NULL", cancellationToken);

            HashSet<string> movementColumns = await ColumnsAsync(connection, "CylinderMovements", cancellationToken);
            await AddColumnAsync(connection, movementColumns, "CylinderMovements", "VehiclePlate", "TEXT NULL", cancellationToken);
            await AddColumnAsync(connection, movementColumns, "CylinderMovements", "IsFull", "INTEGER NULL", cancellationToken);
            await AddColumnAsync(connection, movementColumns, "CylinderMovements", "LegacyId", "INTEGER NULL", cancellationToken);
            await AddColumnAsync(connection, movementColumns, "CylinderMovements", "LegacySource", "TEXT NULL", cancellationToken);

            HashSet<string> customerColumns = await ColumnsAsync(connection, "Customers", cancellationToken);
            await AddColumnAsync(connection, customerColumns, "Customers", "PartnerKey", "TEXT NULL", cancellationToken);
            await AddColumnAsync(connection, customerColumns, "Customers", "LegacyId", "INTEGER NULL", cancellationToken);
            await AddColumnAsync(connection, customerColumns, "Customers", "LegacySource", "TEXT NULL", cancellationToken);
            await AddColumnAsync(connection, cylinderColumns, "Cylinders", "LegacyId", "INTEGER NULL", cancellationToken);
            await AddColumnAsync(connection, cylinderColumns, "Cylinders", "LegacySource", "TEXT NULL", cancellationToken);

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = AuditTableSql;
            await command.ExecuteNonQueryAsync(cancellationToken);

            await using SqliteCommand fills = connection.CreateCommand();
            fills.CommandText = """
CREATE TABLE IF NOT EXISTS CylinderFills (
 Id INTEGER NOT NULL CONSTRAINT PK_CylinderFills PRIMARY KEY AUTOINCREMENT,
 CylinderId INTEGER NOT NULL,
 SerialNumber TEXT NOT NULL,
 GasType TEXT NOT NULL,
 FilledAt TEXT NOT NULL,
 FilledBy TEXT NOT NULL,
 LotNumber TEXT NULL,
 Note TEXT NULL,
 TestOverdue INTEGER NOT NULL DEFAULT 0,
 CONSTRAINT FK_CylinderFills_Cylinders_CylinderId FOREIGN KEY (CylinderId) REFERENCES Cylinders (Id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS IX_CylinderFills_FilledAt ON CylinderFills(FilledAt);
CREATE INDEX IF NOT EXISTS IX_CylinderFills_CylinderId ON CylinderFills(CylinderId);
""";
            await fills.ExecuteNonQueryAsync(cancellationToken);

            await using SqliteCommand fieldWork = connection.CreateCommand();
            fieldWork.CommandText = FieldWorkTablesSql;
            await fieldWork.ExecuteNonQueryAsync(cancellationToken);

            HashSet<string> tripColumns = await ColumnsAsync(connection, "CylinderTripCylinders", cancellationToken);
            foreach ((string column, string definition) in new[]
            {
                ("MissingReason", "TEXT NULL"), ("MissingNote", "TEXT NULL"), ("ReasonAt", "TEXT NULL"),
                ("ReasonBy", "TEXT NULL"), ("CheckedAt", "TEXT NULL"), ("CheckedBy", "TEXT NULL"),
                ("IsPickup", "INTEGER NOT NULL DEFAULT 0"), ("PickupCustomer", "TEXT NULL"),
                ("DeliveredCustomerId", "INTEGER NULL"), ("PickupCustomerId", "INTEGER NULL")
            })
            {
                await AddColumnAsync(connection, tripColumns, "CylinderTripCylinders", column, definition, cancellationToken);
            }

            await AddColumnAsync(connection, await ColumnsAsync(connection, "CylinderTrips", cancellationToken), "CylinderTrips",
                "RequireReasons", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await AddColumnAsync(connection, await ColumnsAsync(connection, "CylinderTrips", cancellationToken), "CylinderTrips",
                "ClosedBy", "TEXT NULL", cancellationToken);

            // Two users loading the same cylinder onto a trip at once must not create two rows. Skipped (and retried at
            // the next start) while old duplicate rows exist, so existing data is never deleted.
            await using SqliteCommand unique = connection.CreateCommand();
            unique.CommandText = "SELECT COUNT(*) FROM (SELECT 1 FROM CylinderTripCylinders GROUP BY TripId, CylinderId HAVING COUNT(*) > 1);";
            if (Convert.ToInt64(await unique.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                unique.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS UX_CylinderTripCylinders_TripId_CylinderId ON CylinderTripCylinders(TripId, CylinderId);";
                await unique.ExecuteNonQueryAsync(cancellationToken);
            }
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
