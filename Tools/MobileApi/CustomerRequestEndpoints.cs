using System.Globalization;
using ArsanGazERP.Models;
using ArsanGazERP.Services;

namespace ArsanGazERP.MobileApi;

internal sealed record CustomerRequestClose(string? Durum, string? Not);
internal sealed record CustomerRequestStaffCreate(int MusteriId, string? Tur, int? Adet, string? Not);

// Panel Müşteri Talepleri tab. There is no customer portal in this edition, so the staff also record the requests that
// customers phone in; a delivery or pickup closes the customer's open requests of that kind.
internal static class CustomerRequestEndpoints
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    internal static void MapPanel(RouteGroupBuilder api)
    {
        api.MapGet("/musteri-talepleri", async (string? durum, int? gun) =>
        {
            List<CustomerRequestView> rows = await CustomerRequests.ListAsync(null, durum != "tumu", gun ?? 30);
            return Results.Ok(rows.Select(RequestRow));
        }).Requires(PanelPermissions.CustomerRequests);

        api.MapPost("/musteri-talebi/{id:int}", async (int id, CustomerRequestClose request, HttpContext context) =>
        {
            try
            {
                string user = Account(context).UserName;
                CustomerRequest closed = await CustomerRequests.CloseAsync(id, request.Durum ?? string.Empty, "Web panel (" + user + ")", request.Not);
                WebPanelAudit.Write(user, "Müşteri talebi " + CustomerRequests.StatusLabel(closed.Status).ToLower(Turkish), $"Talep #{id}",
                    CustomerRequests.CleanNote(request.Not), WebPanelAuth.ClientIp(context));
                return Results.Ok(new { message = $"Talep #{id} {CustomerRequests.StatusLabel(closed.Status).ToLower(Turkish)} olarak işaretlendi." });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.CustomerRequests);

        api.MapPost("/musteri-talebi", async (CustomerRequestStaffCreate request, HttpContext context) =>
        {
            try
            {
                string user = Account(context).UserName;
                int customerId = await new CrudService().ResolveCustomerIdAsync(request.MusteriId) ?? request.MusteriId;
                CustomerRequest created = await CustomerRequests.CreateAsync(customerId, request.Tur ?? string.Empty, request.Adet, request.Not,
                    "Web panel (" + user + ")");
                WebPanelAudit.Write(user, "Müşteri talebi kaydedildi", $"Talep #{created.Id}", CustomerRequests.KindLabel(created.Kind), WebPanelAuth.ClientIp(context));
                return Results.Ok(new { message = $"{CustomerRequests.KindLabel(created.Kind)} talebi kaydedildi (#{created.Id}).", id = created.Id });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.CustomerRequests);
    }

    // With a delivery or pickup on a trip, the customer's open requests of that kind are completed.
    internal static async Task<string> CompleteAsync(int? customerId, string kind, int tripId)
    {
        if (customerId is not int id)
        {
            return string.Empty;
        }

        try
        {
            int closed = await CustomerRequests.CompleteForCustomerAsync(id, kind, "Sefer #" + tripId,
                $"Seferde {(kind == CustomerRequests.Pickup ? "boş alım" : "teslim")} okutuldu (sefer #{tripId}).");
            return closed > 0
                ? $"\nMüşterinin {closed} açık talebi ({CustomerRequests.KindLabel(kind).ToLower(Turkish)}) tamamlandı."
                : string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.WriteLine($"Müşteri talepleri tamamlanamadı (sefer #{tripId}): {exception.Message}");
            return string.Empty;
        }
    }

    private static object RequestRow(CustomerRequestView r) => new
    {
        r.Id,
        r.CustomerId,
        r.CustomerName,
        r.City,
        r.District,
        r.Kind,
        r.KindLabel,
        r.Quantity,
        r.Note,
        Created = r.CreatedAt.ToString("dd.MM.yyyy HH:mm", Turkish),
        r.CreatedBy,
        r.Status,
        r.StatusLabel,
        Handled = r.HandledAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
        r.HandledBy,
        r.HandledNote
    };

    private static WebPanelUser Account(HttpContext context) => (WebPanelUser)context.Items["panelAccount"]!;
}
