using System.Globalization;
using System.Text;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArsanGazERP.Services;

/// <summary>
/// One-way pull sync: ArsanTüpTakip PostgreSQL → ERP database. Never writes to TupTakip.
/// Phase 1: PartnerKey=yalcin customers, their cylinders, and recent movements.
/// </summary>
public sealed class TupTakipPullSync
{
    public const string ConnectionVariable = "ARSANGAZ_TUPTAKIP_SYNC_CONNECTION";
    public const string LegacySourceName = "tuptakip";
    public const string PartnerKeyYalcin = "yalcin";
    public static readonly TimeSpan MovementLookback = TimeSpan.FromDays(90);
    private const int MaxMovementsPerRun = 50_000;

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string? ReadConnectionString() =>
        Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } value ? value.Trim() : null;

    public async Task<TupTakipSyncRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!await Gate.WaitAsync(0, cancellationToken))
        {
            return TupTakipSyncRunResult.Skipped("Önceki senkron turu hâlâ çalışıyor.");
        }

        try
        {
            return await RunCoreAsync(cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<TupTakipSyncRunResult> RunCoreAsync(CancellationToken cancellationToken)
    {
        string? connectionString = ReadConnectionString();
        if (connectionString is null)
        {
            return TupTakipSyncRunResult.Skipped($"{ConnectionVariable} tanımlı değil; TupTakip senkronu atlandı.");
        }

        DateTime started = DateTime.UtcNow;
        int customersInserted = 0, customersUpdated = 0, customersSkipped = 0;
        int cylindersInserted = 0, cylindersUpdated = 0, cylindersSkipped = 0;
        int movementsInserted = 0, movementsUpdated = 0;

        await using NpgsqlConnection source = new(connectionString);
        await source.OpenAsync(cancellationToken);

        List<TtCustomer> ttCustomers = await LoadYalcinCustomersAsync(source, cancellationToken);
        if (ttCustomers.Count == 0)
        {
            return new TupTakipSyncRunResult(true, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, started, DateTime.UtcNow);
        }

        await using ArsanGazDbContext erp = new();
        Dictionary<int, int> ttToErpCustomer = new();

        foreach (TtCustomer tt in ttCustomers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Customer? target = await FindErpCustomerAsync(erp, tt, cancellationToken);
            if (target is null)
            {
                target = new Customer
                {
                    Code = await NextCustomerCodeAsync(erp, tt, cancellationToken),
                    PartnerKey = PartnerKeyYalcin,
                    LegacySource = LegacySourceName,
                    LegacyId = tt.Id,
                    CreatedAt = DateTime.UtcNow
                };
                erp.Customers.Add(target);
                customersInserted++;
            }
            else if (!CanLinkCustomer(target))
            {
                customersSkipped++;
                continue;
            }
            else
            {
                customersUpdated++;
            }

            ApplyCustomerFields(target, tt);
            await erp.SaveChangesAsync(cancellationToken);
            ttToErpCustomer[tt.Id] = target.Id;
        }

        int[] ttCustomerIds = ttCustomers.Select(c => c.Id).ToArray();
        List<TtCylinder> ttCylinders = await LoadCylindersAsync(source, ttCustomerIds, cancellationToken);
        Dictionary<int, int> ttToErpCylinder = new();

        foreach (TtCylinder tt in ttCylinders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(tt.SerialNumber))
            {
                cylindersSkipped++;
                continue;
            }

            Cylinder? target = await FindErpCylinderAsync(erp, tt, cancellationToken);
            if (target is null)
            {
                target = new Cylinder
                {
                    SerialNumber = tt.SerialNumber.Trim(),
                    Barcode = string.IsNullOrWhiteSpace(tt.Barcode) ? tt.SerialNumber.Trim() : tt.Barcode.Trim(),
                    LegacySource = LegacySourceName,
                    LegacyId = tt.Id,
                    CreatedAt = DateTime.UtcNow
                };
                erp.Cylinders.Add(target);
                cylindersInserted++;
            }
            else
            {
                cylindersUpdated++;
            }

            ApplyCylinderFields(target, tt, ttToErpCustomer);
            try
            {
                await erp.SaveChangesAsync(cancellationToken);
                ttToErpCylinder[tt.Id] = target.Id;
            }
            catch (DbUpdateException)
            {
                erp.Entry(target).State = EntityState.Detached;
                cylindersSkipped++;
            }
        }

        if (ttToErpCylinder.Count > 0)
        {
            DateTime movementSince = DateTime.UtcNow.Subtract(MovementLookback);
            List<TtMovement> ttMovements = await LoadMovementsAsync(source, ttToErpCylinder.Keys.ToArray(), movementSince, cancellationToken);
            foreach (TtMovement tt in ttMovements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ttToErpCylinder.TryGetValue(tt.CylinderId, out int erpCylinderId))
                {
                    continue;
                }

                CylinderMovement? target = await erp.CylinderMovements
                    .FirstOrDefaultAsync(m => m.LegacySource == LegacySourceName && m.LegacyId == tt.Id, cancellationToken);
                if (target is null)
                {
                    target = new CylinderMovement
                    {
                        LegacySource = LegacySourceName,
                        LegacyId = tt.Id,
                        CylinderId = erpCylinderId
                    };
                    erp.CylinderMovements.Add(target);
                    movementsInserted++;
                }
                else
                {
                    movementsUpdated++;
                }

                target.FromStatus = (CylinderStatus)tt.FromStatus;
                target.ToStatus = (CylinderStatus)tt.ToStatus;
                target.MovementType = tt.MovementType ?? string.Empty;
                target.VehiclePlate = tt.VehiclePlate;
                target.IsFull = tt.IsFull;
                target.DocumentNumber = tt.DocumentNumber;
                target.Description = tt.Description;
                target.MovementDate = tt.MovementDate;
                target.PerformedBy = tt.PerformedBy;
                target.CustomerId = tt.CustomerId is int ttCust && ttToErpCustomer.TryGetValue(ttCust, out int erpCust) ? erpCust : null;
                target.CylinderId = erpCylinderId;
            }

            await erp.SaveChangesAsync(cancellationToken);
        }

        return new TupTakipSyncRunResult(
            true,
            null,
            customersInserted,
            customersUpdated,
            customersSkipped,
            cylindersInserted,
            cylindersUpdated,
            cylindersSkipped,
            movementsInserted,
            movementsUpdated,
            ttCustomers.Count,
            started,
            DateTime.UtcNow);
    }

    private static bool CanLinkCustomer(Customer existing) =>
        existing.PartnerKey is null or "" or PartnerKeyYalcin;

    private static async Task<Customer?> FindErpCustomerAsync(ArsanGazDbContext erp, TtCustomer tt, CancellationToken cancellationToken)
    {
        Customer? byLegacy = await erp.Customers.FirstOrDefaultAsync(
            c => c.LegacySource == LegacySourceName && c.LegacyId == tt.Id, cancellationToken);
        if (byLegacy is not null)
        {
            return byLegacy;
        }

        string? tax = NormalizeTax(tt.TaxNumber);
        if (tax is not null)
        {
            Customer? byTax = await erp.Customers.FirstOrDefaultAsync(c => c.TaxNumber == tax, cancellationToken);
            if (byTax is not null)
            {
                return byTax;
            }
        }

        string key = CustomerNameKey(tt.CompanyName);
        List<Customer> candidates = await erp.Customers
            .Where(c => c.PartnerKey == PartnerKeyYalcin || c.PartnerKey == null || c.PartnerKey == "")
            .ToListAsync(cancellationToken);
        return candidates.FirstOrDefault(c => CustomerNameKey(c.CompanyName) == key);
    }

    private static async Task<Cylinder?> FindErpCylinderAsync(ArsanGazDbContext erp, TtCylinder tt, CancellationToken cancellationToken)
    {
        Cylinder? byLegacy = await erp.Cylinders.FirstOrDefaultAsync(
            c => c.LegacySource == LegacySourceName && c.LegacyId == tt.Id, cancellationToken);
        if (byLegacy is not null)
        {
            return byLegacy;
        }

        string serial = tt.SerialNumber.Trim();
        Cylinder? bySerial = await erp.Cylinders.FirstOrDefaultAsync(
            c => c.SerialNumber == serial, cancellationToken);
        if (bySerial is not null)
        {
            return bySerial;
        }

        if (!string.IsNullOrWhiteSpace(tt.Barcode))
        {
            string barcode = tt.Barcode.Trim();
            return await erp.Cylinders.FirstOrDefaultAsync(c => c.Barcode == barcode, cancellationToken);
        }

        return null;
    }

    private static void ApplyCustomerFields(Customer target, TtCustomer tt)
    {
        target.CompanyName = Trim(tt.CompanyName, 200) ?? target.CompanyName;
        target.ContactName = Trim(tt.ContactName, 150);
        target.Phone = Trim(tt.Phone, 50);
        target.Email = Trim(tt.Email, 200);
        target.City = Trim(tt.City, 100);
        target.District = Trim(tt.District, 100);
        target.Address = Trim(tt.Address, 500);
        target.TaxOffice = Trim(tt.TaxOffice, 100);
        target.TaxNumber = NormalizeTax(tt.TaxNumber);
        target.IsActive = tt.IsActive;
        target.PartnerKey = PartnerKeyYalcin;
        target.LegacySource = LegacySourceName;
        target.LegacyId = tt.Id;
        target.UpdatedAt = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(target.Code))
        {
            target.Code = $"TT-{tt.Id}";
        }
    }

    private static void ApplyCylinderFields(Cylinder target, TtCylinder tt, IReadOnlyDictionary<int, int> ttToErpCustomer)
    {
        target.GasType = Trim(tt.GasType, 100) ?? target.GasType;
        target.Capacity = tt.Capacity;
        target.CapacityUnit = string.IsNullOrWhiteSpace(tt.CapacityUnit) ? target.CapacityUnit : tt.CapacityUnit.Trim();
        target.Status = (CylinderStatus)tt.Status;
        target.IsFull = tt.IsFull;
        target.VehiclePlate = Trim(tt.VehiclePlate, 30);
        target.LastMovementAt = tt.LastMovementAt;
        target.IsActive = tt.IsActive;
        target.LegacySource = LegacySourceName;
        target.LegacyId = tt.Id;
        target.CurrentCustomerId = tt.CurrentCustomerId is int cur && ttToErpCustomer.TryGetValue(cur, out int erpCur) ? erpCur : null;
        target.OwnerCustomerId = tt.OwnerCustomerId is int own && ttToErpCustomer.TryGetValue(own, out int erpOwn) ? erpOwn : null;
        if (!string.IsNullOrWhiteSpace(tt.Barcode))
        {
            target.Barcode = tt.Barcode.Trim();
        }
    }

    private static async Task<string> NextCustomerCodeAsync(ArsanGazDbContext erp, TtCustomer tt, CancellationToken cancellationToken)
    {
        string? fromTt = Trim(tt.Code, 50);
        if (fromTt is not null && !await erp.Customers.AnyAsync(c => c.Code == fromTt, cancellationToken))
        {
            return fromTt;
        }

        string fallback = $"TT-{tt.Id}";
        if (!await erp.Customers.AnyAsync(c => c.Code == fallback, cancellationToken))
        {
            return fallback;
        }

        string prefix = $"TT-{tt.Id}-";
        List<string> used = await erp.Customers.AsNoTracking().Where(c => c.Code.StartsWith(prefix)).Select(c => c.Code).ToListAsync(cancellationToken);
        int next = used.Select(c => int.TryParse(c.AsSpan(prefix.Length), out int n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
        return prefix + next;
    }

    private static string CustomerNameKey(string? name) =>
        string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpper(Turkish);

    private static string? NormalizeTax(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? Trim(value, 20) : Trim(digits, 20);
    }

    private static string? Trim(string? value, int max)
    {
        string text = string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            return null;
        }

        return text.Length > max ? text[..max] : text;
    }

    private static async Task<List<TtCustomer>> LoadYalcinCustomersAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, code, companyname, contactname, phone, email, city, district, address, taxoffice, taxnumber, isactive
            FROM tuptakip.customers
            WHERE partnerkey = @partner
            ORDER BY id
            """;
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("partner", PartnerKeyYalcin);
        return await ReadCustomersAsync(command, cancellationToken);
    }

    private static async Task<List<TtCylinder>> LoadCylindersAsync(NpgsqlConnection connection, int[] ttCustomerIds, CancellationToken cancellationToken)
    {
        if (ttCustomerIds.Length == 0)
        {
            return [];
        }

        const string sql = """
            SELECT id, serialnumber, barcode, gastype, capacity, capacityunit, status, isfull, vehicleplate,
                   currentcustomerid, ownercustomerid, lastmovementat, isactive
            FROM tuptakip.cylinders
            WHERE isactive
              AND (currentcustomerid = ANY(@ids) OR ownercustomerid = ANY(@ids))
            ORDER BY id
            """;
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("ids", ttCustomerIds);
        return await ReadCylindersAsync(command, cancellationToken);
    }

    private static async Task<List<TtMovement>> LoadMovementsAsync(
        NpgsqlConnection connection,
        int[] ttCylinderIds,
        DateTime sinceUtc,
        CancellationToken cancellationToken)
    {
        if (ttCylinderIds.Length == 0)
        {
            return [];
        }

        const string sql = """
            SELECT id, cylinderid, customerid, fromstatus, tostatus, movementtype, vehicleplate, isfull,
                   documentnumber, description, movementdate, performedby
            FROM tuptakip.cylindermovements
            WHERE cylinderid = ANY(@ids) AND movementdate >= @since
            ORDER BY id
            LIMIT @limit
            """;
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("ids", ttCylinderIds);
        command.Parameters.AddWithValue("since", sinceUtc);
        command.Parameters.AddWithValue("limit", MaxMovementsPerRun);
        return await ReadMovementsAsync(command, cancellationToken);
    }

    private static async Task<List<TtCustomer>> ReadCustomersAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        List<TtCustomer> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TtCustomer(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetBoolean(11)));
        }

        return rows;
    }

    private static async Task<List<TtCylinder>> ReadCylindersAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        List<TtCylinder> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TtCylinder(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetBoolean(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.IsDBNull(11) ? null : reader.GetDateTime(11),
                reader.GetBoolean(12)));
        }

        return rows;
    }

    private static async Task<List<TtMovement>> ReadMovementsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        List<TtMovement> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TtMovement(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetBoolean(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetDateTime(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return rows;
    }

    private sealed record TtCustomer(
        int Id,
        string Code,
        string CompanyName,
        string? ContactName,
        string? Phone,
        string? Email,
        string? City,
        string? District,
        string? Address,
        string? TaxOffice,
        string? TaxNumber,
        bool IsActive);

    private sealed record TtCylinder(
        int Id,
        string SerialNumber,
        string? Barcode,
        string GasType,
        decimal Capacity,
        string? CapacityUnit,
        int Status,
        bool? IsFull,
        string? VehiclePlate,
        int? CurrentCustomerId,
        int? OwnerCustomerId,
        DateTime? LastMovementAt,
        bool IsActive);

    private sealed record TtMovement(
        int Id,
        int CylinderId,
        int? CustomerId,
        int FromStatus,
        int ToStatus,
        string? MovementType,
        string? VehiclePlate,
        bool? IsFull,
        string? DocumentNumber,
        string? Description,
        DateTime MovementDate,
        string? PerformedBy);
}

public sealed record TupTakipSyncRunResult(
    bool Ran,
    string? SkipReason,
    int CustomersInserted,
    int CustomersUpdated,
    int CustomersSkipped,
    int CylindersInserted,
    int CylindersUpdated,
    int CylindersSkipped,
    int MovementsInserted,
    int MovementsUpdated,
    int SourceCustomerCount,
    DateTime StartedUtc,
    DateTime FinishedUtc)
{
    public static TupTakipSyncRunResult Skipped(string reason) =>
        new(false, reason, 0, 0, 0, 0, 0, 0, 0, 0, 0, DateTime.UtcNow, DateTime.UtcNow);

    public string SummaryLine()
    {
        if (SkipReason is not null)
        {
            return SkipReason;
        }

        StringBuilder text = new();
        text.Append($"TupTakip→ERP: {SourceCustomerCount} müşteri kaynağı; ");
        text.Append($"müşteri +{CustomersInserted}/~{CustomersUpdated}/atlanan {CustomersSkipped}; ");
        text.Append($"tüp +{CylindersInserted}/~{CylindersUpdated}/atlanan {CylindersSkipped}; ");
        text.Append($"hareket +{MovementsInserted}/~{MovementsUpdated}.");
        return text.ToString();
    }
}
