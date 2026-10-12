using System.Globalization;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.MobileApi;

internal sealed record VehicleSaveRequest(string? Plate, string? Name, string? VehicleType, string? DriverName, int? CylinderCapacity, string? Note, bool? IsActive)
{
    public VehicleInput Input => new(Plate, Name, VehicleType, DriverName, CylinderCapacity, Note, IsActive);
}

// Vehicle registry for the mobile app (/api/arac-kayit) and shared JSON shapes used by the web panel as well.
// /api/araclar (plain plate list) stays as it is for older app versions.
internal static class VehicleEndpoints
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/arac-kayit", async (bool? pasif) => Results.Ok((await new VehicleService().ListAsync(pasif == true)).Select(VehicleRow)));

        app.MapGet("/api/arac-kayit/{id:int}", async (int id) =>
        {
            VehicleDetail? detail = await new VehicleService().DetailAsync(id);
            return detail is null ? Results.NotFound(new { error = "Araç bulunamadı." }) : Results.Ok(DetailDto(detail));
        });

        app.MapPost("/api/arac-kayit", async (VehicleSaveRequest request) =>
            await SaveAsync(() => new VehicleService().CreateAsync(request.Input), v => $"{v.Plate} araç olarak kaydedildi."));

        app.MapPost("/api/arac-kayit/{id:int}", async (int id, VehicleSaveRequest request) =>
            await SaveAsync(() => new VehicleService().UpdateAsync(id, request.Input), v => $"{v.Plate} güncellendi." + (v.IsActive ? string.Empty : " Araç pasif; yeni seferlerde seçilemez.")));

        app.MapGet("/api/sefer/{tripId:int}/yuk", async (int tripId, string? plaka) => await TripLoadAsync(tripId, plaka));
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
        Created = v.CreatedAt.ToString("dd.MM.yyyy", Turkish)
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
