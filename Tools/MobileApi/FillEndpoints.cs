using ArsanGazERP.Services;

namespace ArsanGazERP.MobileApi;

internal sealed record FillRequest(List<string?>? Codes, string? Lot, string? Note, bool AllowOverdueTest = false);

internal static class FillEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/dolum", async (FillRequest request, HttpContext context) =>
        {
            try
            {
                FillResult result = await CylinderFills.FillAsync(request.Codes ?? [], request.Lot, request.Note,
                    "Mobil (" + MobileEndpoints.DeviceName(context) + ")", request.AllowOverdueTest);
                return Results.Ok(Dto(result));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.FillRecord);

        app.MapGet("/api/dolum/son", async (int? gun) => Results.Ok((await CylinderFills.ListAsync(gun ?? 1, null, 200)).Select(Row)))
            .Requires(PanelPermissions.FillRecord);
    }

    internal static object Dto(FillResult result) => new
    {
        message = $"{result.Filled} tüp dolum kaydı yapıldı." + (result.Lines.Any(l => !l.Ok) ? $" {result.Lines.Count(l => !l.Ok)} tüp kaydedilmedi." : string.Empty),
        result.Filled,
        lines = result.Lines.Select(l => new { l.Code, l.Ok, l.Message, l.TestOverdue }),
        needsTestConfirmation = result.NeedsTestConfirmation
    };

    internal static object Row(FillListItem x) => new
    {
        x.Id,
        x.CylinderId,
        x.SerialNumber,
        x.GasType,
        FilledAt = x.FilledAt.ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.GetCultureInfo("tr-TR")),
        x.FilledBy,
        x.LotNumber,
        x.Note,
        x.TestOverdue
    };
}
