using System.Globalization;
using ArsanTupTakip.Models;
using ArsanTupTakip.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanTupTakip.Api;

internal sealed record VehicleSaveRequest(string? Plate, string? Name, string? VehicleType, string? DriverName, int? CylinderCapacity, string? Note, bool? IsActive, uint? Version = null)
{
    public VehicleInput Input => new(Plate, Name, VehicleType, DriverName, CylinderCapacity, Note, IsActive);
}

// Vehicle registry for the mobile app (/api/arac-kayit) and shared JSON shapes used by the web panel as well.
internal static class VehicleEndpoints
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/arac-kayit", async (bool? pasif) => Results.Ok((await new VehicleService().ListAsync(pasif == true)).Select(VehicleRow)))
            .Requires(Perm.VehicleView, Perm.TripStart);

        app.MapGet("/api/arac-kayit/{id:int}", async (int id) =>
        {
            VehicleDetail? detail = await new VehicleService().DetailAsync(id);
            return detail is null ? Results.NotFound(new { error = "Araç bulunamadı." }) : Results.Ok(DetailDto(detail));
        }).Requires(Perm.VehicleView);

        app.MapPost("/api/arac-kayit", async (VehicleSaveRequest request) =>
            await SaveAsync(() => new VehicleService().CreateAsync(request.Input), v => $"{v.Plate} araç olarak kaydedildi."))
            .Requires(Perm.VehicleAdd);

        app.MapPost("/api/arac-kayit/{id:int}", async (int id, VehicleSaveRequest request, HttpContext context) => await UpdateAsync(id, request, context))
            .Requires(Perm.VehicleEdit, Perm.VehicleDeactivate);

        app.MapGet("/api/sefer/{tripId:int}/yuk", async (int tripId, string? plaka) => await TripLoadAsync(tripId, plaka)).Requires(Perm.TripView);
    }

    // Editing details needs VehicleEdit, switching active/passive needs VehicleDeactivate.
    internal static async Task<IResult> UpdateAsync(int id, VehicleSaveRequest request, HttpContext context, Action<Vehicle>? audit = null)
    {
        WebPanelUser user = (WebPanelUser)context.Items["panelAccount"]!;
        await using (ArsanTupTakip.Database.TupTakipDbContext db = new())
        {
            Vehicle? existing = await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (existing is null)
            {
                return Results.NotFound(new { error = "Araç bulunamadı." });
            }

            bool activeChanges = request.IsActive is bool active && active != existing.IsActive;
            bool detailsChange = VehicleTracking.Clean(request.Name) != existing.Name || VehicleTracking.Clean(request.VehicleType) != existing.VehicleType
                || VehicleTracking.Clean(request.DriverName) != existing.DriverName || VehicleTracking.Clean(request.Note) != existing.Note
                || (request.CylinderCapacity is > 0 ? request.CylinderCapacity : null) != existing.CylinderCapacity;
            HashSet<string> mine = Perm.Effective(user);
            if (activeChanges && !mine.Contains(Perm.VehicleDeactivate))
            {
                return PermissionEndpointExtensions.Denied(Perm.VehicleDeactivate);
            }

            if (detailsChange && !mine.Contains(Perm.VehicleEdit))
            {
                return PermissionEndpointExtensions.Denied(Perm.VehicleEdit);
            }
        }

        return await SaveAsync(async () =>
        {
            Vehicle vehicle = await new VehicleService().UpdateAsync(id, request.Input, request.Version);
            audit?.Invoke(vehicle);
            return vehicle;
        }, v => $"{v.Plate} güncellendi." + (v.IsActive ? string.Empty : " Araç pasif; yeni seferlerde seçilemez."));
    }

    internal static async Task<IResult> SaveAsync(Func<Task<Vehicle>> save, Func<Vehicle, string> message)
    {
        try
        {
            Vehicle vehicle = await save();
            return Results.Ok(new { message = message(vehicle), vehicle.Id, vehicle.Plate });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (DbUpdateException)
        {
            return Results.BadRequest(new { error = "Bu plaka zaten kayıtlı." });
        }
    }

    internal static async Task<IResult> TripLoadAsync(int tripId, string? plate)
    {
        try
        {
            TripLoadView? load = await new CylinderTripService().TripLoadAsync(tripId, plate);
            return load is null
                ? Results.NotFound(new { error = "Sefer bulunamadı." })
                : Results.Ok(new { load.TripId, load.Title, load.Active, load.Plate, load.Total, load.OnVehicle, load.Missing, Groups = load.Groups.Select(GroupDto) });
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    internal static object VehicleRow(VehicleListItem v) => new
    {
        v.Id,
        v.Plate,
        v.Name,
        v.VehicleType,
        v.DriverName,
        v.CylinderCapacity,
        v.Note,
        v.IsActive,
        v.CylindersOnBoard,
        v.ActiveTripId,
        v.ActiveTripTitle,
        Created = v.CreatedAt.ToString("dd.MM.yyyy", Turkish),
        v.Version
    };

    internal static object DetailDto(VehicleDetail d) => new
    {
        Vehicle = VehicleRow(d.Vehicle),
        OnBoard = d.OnBoard.Select(GroupDto),
        d.CapacityWarning,
        Trips = d.Trips.Select(t => new
        {
            t.TripId,
            t.Title,
            t.Active,
            Started = t.StartedAt.ToString("dd.MM.yyyy HH:mm", Turkish),
            Closed = t.ClosedAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
            VehicleReturned = t.ReturnedAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
            t.Loaded,
            t.Delivered,
            t.Returned,
            t.Missing
        })
    };

    internal static object GroupDto(GasTypeGroup g) => new
    {
        g.GasType,
        g.Count,
        g.Full,
        g.Empty,
        g.Alarm,
        Summary = VehicleTracking.GroupSummary(g),
        Cylinders = g.Cylinders.Select(c => new
        {
            c.CylinderId,
            c.SerialNumber,
            c.Barcode,
            c.GasType,
            c.Capacity,
            c.Fullness,
            c.StatusName,
            c.Customer,
            c.Plate,
            LastMovement = c.LastMovementAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
            TestDue = c.TestDueDate?.ToString("dd.MM.yyyy", Turkish),
            TestOverdue = c.TestDueDate is DateTime due && due.Date < DateTime.Today,
            Loaded = c.LoadedAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
            c.State,
            c.Alarm
        })
    };
}
