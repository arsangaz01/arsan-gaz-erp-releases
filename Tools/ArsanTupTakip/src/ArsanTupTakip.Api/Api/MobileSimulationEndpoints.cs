namespace ArsanTupTakip.Api;

internal sealed record SimulationPermissionRequest(string? Key, bool Granted);

// Mobile "Kullanıcı gözünden": a Yönetici picks a user, the phone redraws its screens with that user's permissions,
// and single permissions can be granted or revoked on the spot (same rules and audit as the panel's user editor).
// Requests made while simulating still run with the Yönetici's own session.
internal static class MobileSimulationEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/simulasyon/kullanicilar", (HttpContext context) =>
        {
            if (AdminOnly(context, out WebPanelUser admin) is IResult denied)
            {
                return denied;
            }

            return Results.Ok(WebPanelUserStore.All()
                .Where(u => u.DeletedAt is null && u.Id != admin.Id)
                .OrderByDescending(u => u.IsActive)
                .ThenBy(u => Array.IndexOf(PanelRoles.All, u.Role))
                .ThenBy(u => u.UserName, StringComparer.OrdinalIgnoreCase)
                .Select(u => new { u.Id, u.UserName, u.DisplayName, u.Role, u.IsActive, u.FixedPlate }));
        }).Requires(Perm.ManageUsers);

        app.MapGet("/api/simulasyon/{id:int}", (int id, HttpContext context) =>
        {
            if (AdminOnly(context, out _) is IResult denied)
            {
                return denied;
            }

            return WebPanelUserStore.Find(id) is WebPanelUser user && user.DeletedAt is null
                ? Results.Ok(View(user, []))
                : Results.NotFound(new { error = "Kullanıcı bulunamadı." });
        }).Requires(Perm.ManageUsers);

        app.MapPost("/api/simulasyon/{id:int}/yetki", (int id, SimulationPermissionRequest request, HttpContext context, WebPanelAuth auth) =>
        {
            if (AdminOnly(context, out WebPanelUser admin) is IResult denied)
            {
                return denied;
            }

            if (WebPanelUserStore.Find(id) is not WebPanelUser user || user.DeletedAt is not null)
            {
                return Results.NotFound(new { error = "Kullanıcı bulunamadı." });
            }

            if (request.Key is not string key || !Perm.ByKey.ContainsKey(key))
            {
                return Results.BadRequest(new { error = "Bilinmeyen yetki." });
            }

            if (user.Role == PanelRoles.Admin)
            {
                return Results.BadRequest(new { error = "Yönetici her zaman tüm yetkilere sahiptir; yetkileri tek tek değiştirilemez." });
            }

            HashSet<string> before = Perm.Effective(user);
            HashSet<string> wanted = [.. before];
            if (request.Granted)
            {
                AddWithPrerequisites(wanted, key);
            }
            else
            {
                wanted.Remove(key);
            }

            string? error = auth.UpdateUser(admin, WebPanelAuth.ClientIp(context), id, user.DisplayName, user.Role, user.IsActive, [.. wanted]);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            WebPanelUser updated = WebPanelUserStore.Find(id) ?? user;
            HashSet<string> after = Perm.Effective(updated);
            List<string> changes =
            [
                .. after.Except(before).Select(k => "+ " + Perm.ByKey[k].Label),
                .. before.Except(after).Select(k => "− " + Perm.ByKey[k].Label)
            ];
            return Results.Ok(View(updated, changes));
        }).Requires(Perm.ManageUsers);
    }

    private static IResult? AdminOnly(HttpContext context, out WebPanelUser user)
    {
        user = (WebPanelUser)context.Items["panelAccount"]!;
        return user.Role == PanelRoles.Admin
            ? null
            : Results.Json(new { error = "Kullanıcı gözünden görüntüleme yalnızca Yönetici içindir." }, statusCode: StatusCodes.Status403Forbidden);
    }

    private static void AddWithPrerequisites(HashSet<string> set, string key)
    {
        if (set.Add(key))
        {
            foreach (string required in Perm.ByKey[key].Requires)
            {
                AddWithPrerequisites(set, required);
            }
        }
    }

    private static object View(WebPanelUser user, List<string> changes)
    {
        HashSet<string> effective = Perm.Effective(user);
        HashSet<string> defaults = [.. Perm.RoleDefaults(user.Role)];
        return new
        {
            id = user.Id,
            user = user.UserName,
            displayName = user.DisplayName,
            role = user.Role,
            roleText = PanelRoles.Description(user.Role),
            isActive = user.IsActive,
            fixedPlate = user.FixedPlate,
            editable = user.Role != PanelRoles.Admin,
            permissions = effective.Order(StringComparer.Ordinal).ToArray(),
            changes,
            groups = Perm.Catalog.Select(g => new
            {
                name = g.Name,
                permissions = g.Permissions.Select(p => new
                {
                    key = p.Key,
                    label = p.Label,
                    description = p.Description,
                    granted = effective.Contains(p.Key),
                    roleDefault = defaults.Contains(p.Key)
                })
            })
        };
    }
}
