using System.Data.Common;
using ArsanTupTakip.Database;
using Npgsql;

namespace ArsanTupTakip.Api;

internal sealed record PermissionInfo(string Key, string Label, string Description, string[] Requires);
internal sealed record PermissionGroup(string Name, PermissionInfo[] Permissions);

// Every action of the panel and the mobile app. Roles are presets; a Yönetici may grant or revoke any single
// permission per user (stored as overrides). The server checks these on every /panel/api and /api call.
internal static class Perm
{
    internal const string PanelLogin = "panel-giris";
    internal const string MobileLogin = "mobil-giris";
    internal const string Summary = "ozet";
    internal const string Reports = "raporlar";
    internal const string CylinderView = "tup-goruntule";
    internal const string CylinderRegister = "tup-kayit";
    internal const string CylinderEdit = "tup-duzenle";
    internal const string CylinderDeactivate = "tup-pasif";
    internal const string CylinderAudit = "tup-sayim";
    internal const string StockCount = "depo-sayim";
    internal const string MoveDeliver = "hareket-teslim";
    internal const string MoveReturn = "hareket-iade";
    internal const string MoveVehicle = "hareket-arac";
    internal const string MoveFilling = "hareket-dolum";
    internal const string MoveService = "hareket-servis";
    internal const string MoveScrap = "hareket-hurda";
    internal const string MoveFullness = "hareket-dolu-bos";
    internal const string MoveBulk = "hareket-toplu";
    internal const string MoveOverride = "hareket-cakisma";
    internal const string MovementHistory = "hareket-gecmisi";
    internal const string Export = "disa-aktar";
    internal const string CustomerView = "musteri-goruntule";
    internal const string CustomerAdd = "musteri-ekle";
    internal const string TripView = "sefer-goruntule";
    internal const string TripStart = "sefer-baslat";
    internal const string TripLoad = "sefer-yukle";
    internal const string TripDeliver = "sefer-teslim";
    internal const string TripReturn = "sefer-donus";
    internal const string TripClose = "sefer-kapat";
    internal const string VehicleView = "arac-goruntule";
    internal const string VehicleAdd = "arac-ekle";
    internal const string VehicleEdit = "arac-duzenle";
    internal const string VehicleDeactivate = "arac-pasif";
    internal const string VehicleDelete = "arac-sil";
    internal const string ManageUsers = "kullanici-yonetimi";
    internal const string ManageDevices = "cihaz-yonetimi";
    internal const string AuditLog = "denetim-kaydi";

    // Status (CylinderStatus value) → permission needed to move a cylinder into it.
    internal static readonly IReadOnlyDictionary<int, string> MoveByStatus = new Dictionary<int, string>
    {
        [1] = MoveReturn, [2] = MoveVehicle, [3] = MoveDeliver, [4] = MoveFilling, [5] = MoveService, [6] = MoveScrap
    };

    internal static readonly string[] AnyMove = [MoveDeliver, MoveReturn, MoveVehicle, MoveFilling, MoveService, MoveScrap];

    // Customer pickers appear in several screens; the full customer list (with phones) needs CustomerView.
    internal static readonly string[] CustomerPicker =
        [CustomerView, CustomerAdd, CylinderView, CylinderRegister, CylinderAudit, MovementHistory, MoveDeliver, TripDeliver];

    private static PermissionInfo P(string key, string label, string description, params string[] requires) => new(key, label, description, requires);

    internal static readonly PermissionGroup[] Catalog =
    [
        new("Giriş", [
            P(PanelLogin, "Web paneline giriş", "Tarayıcıdan arsantuptakip.com/tuptakip/panel adresine giriş yapabilir."),
            P(MobileLogin, "Mobil uygulamaya giriş", "Telefondaki ArsanTüpTakip uygulamasına aynı kullanıcı adı ve şifreyle giriş yapabilir.")
        ]),
        new("Özet ve raporlar", [
            P(Summary, "Özet ve tüp durumu", "Panel Özet sekmesi, mobil Genel özet ve Tüp durumu ekranı."),
            P(Reports, "Raporlar", "Sipariş tahmini, test tarihi yaklaşan, uzun süredir müşteride ve müşterideki tüpler raporları.")
        ]),
        new("Tüpler", [
            P(CylinderView, "Tüpleri görüntüleme", "Tüp listesi, barkodla sorgulama, tüp ayrıntısı ve hareket geçmişi."),
            P(CylinderRegister, "Yeni tüp kaydı", "Envantere yeni tüp ekleyebilir (panel Yeni tüp, mobil ➕ Yeni tüp)."),
            P(CylinderEdit, "Tüp bilgilerini düzenleme", "Gaz türü, kapasite, birim ve test tarihini değiştirebilir.", CylinderView),
            P(CylinderDeactivate, "Tüpü pasife alma / aktif etme", "Tüpü kullanım dışı bırakabilir veya yeniden aktif edebilir.", CylinderView),
            P(CylinderAudit, "Müşteride sayım", "Mobilde müşterideki tüpleri okutup sistemle karşılaştırır."),
            P(StockCount, "Depo sayımı", "Mobilde ana depodaki tüpleri barkodla okutarak sayar, sayımı bitirir; panelde Sayım Raporları sekmesini görür.")
        ]),
        new("Tüp hareketleri", [
            P(MoveDeliver, "Müşteriye teslim (çıkış)", "Tüpü müşteriye teslim edildi olarak kaydeder.", CylinderView),
            P(MoveReturn, "Depoya iade", "Tüpü depoya döndü olarak kaydeder.", CylinderView),
            P(MoveVehicle, "Araca yükleme (taşınıyor)", "Tüpü bir araçta taşınıyor olarak kaydeder.", CylinderView),
            P(MoveFilling, "Dolum", "Tüpü dolumda olarak kaydeder.", CylinderView),
            P(MoveService, "Servis", "Tüpü serviste olarak kaydeder.", CylinderView),
            P(MoveScrap, "Hurda", "Tüpü hurdaya ayrıldı olarak kaydeder.", CylinderView),
            P(MoveFullness, "Dolu / boş seçimi", "Harekette dolu/boş bilgisini elle seçebilir; yoksa otomatik belirlenir."),
            P(MoveBulk, "Toplu hareket", "Mobilde birden çok tüpe aynı hareketi tek seferde uygular."),
            P(MoveOverride, "Başka müşterideki tüpü yine de kaydetme", "Tüp sistemde başka müşteride görünse bile hareketi onaylayıp kaydedebilir."),
            P(MovementHistory, "Hareket geçmişi", "Panel Hareketler sekmesi ve mobil Son hareketler raporu."),
            P(Export, "Excel'e aktarma (CSV)", "Hareket listesini CSV dosyası olarak indirebilir.", MovementHistory)
        ]),
        new("Müşteriler", [
            P(CustomerView, "Müşterileri görüntüleme", "Müşteriler sekmesi ve müşteri telefonları. Yoksa seçim listelerinde yalnızca ad ve şehir görünür."),
            P(CustomerAdd, "Yeni müşteri", "Yeni müşteri kaydı açabilir.")
        ]),
        new("Seferler", [
            P(TripView, "Seferleri görüntüleme", "Sefer listesi, sefer ayrıntısı, araç yükü ve kontrol."),
            P(TripStart, "Sefer başlatma ve sefere araç ekleme", "Kayıtlı araçlardan seçerek sefer açar.", TripView),
            P(TripLoad, "Araca yükleme (sefer)", "Seferdeki araca tüp okutup yükler.", TripView),
            P(TripDeliver, "Seferde müşteriye teslim", "Seferdeki tüpleri müşteriye teslim olarak okutur.", TripView),
            P(TripReturn, "Sefer dönüşü (depoya)", "Dönen aracın tüplerini depoya okutur.", TripView),
            P(TripClose, "Seferi kapatma", "Seferi kapatır; okutulmayan tüpler eksik raporlanır.", TripView)
        ]),
        new("Araçlar", [
            P(VehicleView, "Araçları görüntüleme", "Araç listesi, araçtaki tüpler ve sefer geçmişi."),
            P(VehicleAdd, "Araç ekleme", "Yeni araç kaydı açar.", VehicleView),
            P(VehicleEdit, "Araç düzenleme", "Araç adı, türü, şoför, kapasite ve notu değiştirir.", VehicleView),
            P(VehicleDeactivate, "Aracı pasife alma / aktif etme", "Aracı yeni seferlerde seçilemez yapar veya geri açar.", VehicleView),
            P(VehicleDelete, "Aracı silme", "Araç kaydını kalıcı olarak siler; araçtaki tüpler depoya alınır, sefer geçmişi korunur. Varsayılan olarak yalnızca Yönetici'de vardır.", VehicleView)
        ]),
        new("Yönetim", [
            P(ManageUsers, "Kullanıcı yönetimi", "Kullanıcı ekler, düzenler, pasife alır, siler, şifre sıfırlar ve yetkileri seçer. Yönetici hesaplarına dokunamaz ve kendinde olmayan yetkiyi veremez.", PanelLogin),
            P(ManageDevices, "Cihaz yönetimi", "Giriş yapmış telefonları görür ve oturumlarını kapatır.", PanelLogin),
            P(AuditLog, "Denetim kaydı", "Giriş ve yönetim işlemlerinin kaydını görür.", PanelLogin)
        ])
    ];

    internal static readonly IReadOnlyDictionary<string, PermissionInfo> ByKey =
        Catalog.SelectMany(g => g.Permissions).ToDictionary(p => p.Key);

    internal static readonly string[] All = [.. Catalog.SelectMany(g => g.Permissions).Select(p => p.Key)];

    private static readonly string[] OperatorDefaults =
    [
        PanelLogin, MobileLogin, Summary, Reports, CylinderView, CylinderRegister, CylinderEdit, CylinderDeactivate, CylinderAudit, StockCount,
        MoveDeliver, MoveReturn, MoveVehicle, MoveFilling, MoveService, MoveScrap, MoveFullness, MoveBulk, MoveOverride,
        MovementHistory, Export, CustomerView, CustomerAdd, TripView, TripStart, TripLoad, TripDeliver, TripReturn, TripClose,
        VehicleView, VehicleAdd, VehicleEdit, VehicleDeactivate
    ];

    private static readonly string[] ViewerDefaults =
        [PanelLogin, MobileLogin, Summary, Reports, CylinderView, MovementHistory, Export, CustomerView, TripView, VehicleView];

    internal static IReadOnlyList<string> RoleDefaults(string? role) => role switch
    {
        PanelRoles.Admin => All,
        PanelRoles.Operator => OperatorDefaults,
        PanelRoles.Viewer => ViewerDefaults,
        _ => []
    };

    // Role defaults plus granted minus revoked; a permission whose prerequisites are missing is dropped too.
    // A Yönetici always has everything, so the last administrator can never lock the panel.
    internal static HashSet<string> Effective(string? role, IReadOnlyDictionary<string, bool>? overrides)
    {
        if (role == PanelRoles.Admin)
        {
            return [.. All];
        }

        if (PanelRoles.Normalize(role) is null)
        {
            return [];
        }

        HashSet<string> set = [.. RoleDefaults(role)];
        foreach ((string key, bool granted) in overrides ?? new Dictionary<string, bool>())
        {
            if (!ByKey.ContainsKey(key))
            {
                continue;
            }

            if (granted)
            {
                set.Add(key);
            }
            else
            {
                set.Remove(key);
            }
        }

        bool changed = true;
        while (changed)
        {
            changed = set.RemoveWhere(key => ByKey[key].Requires.Any(r => !set.Contains(r))) > 0;
        }

        return set;
    }

    internal static HashSet<string> Effective(WebPanelUser user) =>
        user.Role == PanelRoles.Admin ? [.. All] : Effective(user.Role, PermissionOverrides.For(user.Id));

    internal static bool Has(WebPanelUser user, string permission) => Effective(user).Contains(permission);

    // Overrides that turn the role defaults into the wanted set (only differences are stored).
    internal static Dictionary<string, bool> Diff(string role, IEnumerable<string> wanted)
    {
        HashSet<string> target = [.. wanted.Where(ByKey.ContainsKey)];
        HashSet<string> defaults = [.. RoleDefaults(role)];
        Dictionary<string, bool> result = [];
        foreach (string key in All)
        {
            if (target.Contains(key) && !defaults.Contains(key))
            {
                result[key] = true;
            }
            else if (!target.Contains(key) && defaults.Contains(key))
            {
                result[key] = false;
            }
        }

        return result;
    }
}

// Endpoint metadata: the caller needs at least one of the listed permissions. Endpoints without this or
// SignedInOnly metadata are refused, so a forgotten mapping fails closed.
internal sealed record RequiresPermission(string[] AnyOf);

// Any signed-in user (also before the forced password change), e.g. /ben, /sifre, /cikis.
internal sealed class SignedInOnly(bool beforePasswordChange = false)
{
    internal bool BeforePasswordChange { get; } = beforePasswordChange;
}

internal static class PermissionEndpointExtensions
{
    internal static TBuilder Requires<TBuilder>(this TBuilder builder, params string[] anyOf) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiresPermission(anyOf));

    internal static TBuilder SignedIn<TBuilder>(this TBuilder builder, bool beforePasswordChange = false) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new SignedInOnly(beforePasswordChange));

    // Shared by the panel filter and the mobile middleware. Returns null when allowed, otherwise the response.
    internal static IResult? Check(HttpContext context, WebPanelUser user)
    {
        EndpointMetadataCollection? metadata = context.GetEndpoint()?.Metadata;
        SignedInOnly? open = metadata?.GetMetadata<SignedInOnly>();
        if (user.MustChangePassword && open?.BeforePasswordChange != true)
        {
            return Results.Json(new { error = "Devam etmeden önce şifrenizi değiştirmelisiniz.", mustChangePassword = true }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (open is not null)
        {
            return null;
        }

        RequiresPermission? required = metadata?.GetMetadata<RequiresPermission>();
        HashSet<string> effective = Perm.Effective(user);
        if (required is null || !required.AnyOf.Any(effective.Contains))
        {
            string wanted = required is null ? "tanımsız" : string.Join(" / ", required.AnyOf.Select(k => Perm.ByKey.TryGetValue(k, out PermissionInfo? p) ? p.Label : k));
            return DeniedText(wanted);
        }

        return null;
    }

    private static IResult DeniedText(string what) =>
        Results.Json(new { error = $"Bu işlem için yetkiniz yok ({what}). Yöneticinizden yetki isteyin.", permission = what }, statusCode: StatusCodes.Status403Forbidden);

    internal static IResult Denied(params string[] keys) =>
        DeniedText(string.Join(" / ", keys.Select(k => Perm.ByKey.TryGetValue(k, out PermissionInfo? p) ? p.Label : k)));
}

internal interface IPermissionOverrideStore
{
    Dictionary<int, Dictionary<string, bool>> LoadAll();
    void Replace(int userId, IReadOnlyDictionary<string, bool> overrides, string actor);
}

internal sealed class MemoryPermissionOverrideStore : IPermissionOverrideStore
{
    private readonly Dictionary<int, Dictionary<string, bool>> _data = [];

    public Dictionary<int, Dictionary<string, bool>> LoadAll()
    {
        lock (_data)
        {
            return _data.ToDictionary(p => p.Key, p => new Dictionary<string, bool>(p.Value));
        }
    }

    public void Replace(int userId, IReadOnlyDictionary<string, bool> overrides, string actor)
    {
        lock (_data)
        {
            _data[userId] = new Dictionary<string, bool>(overrides);
        }
    }
}

// Table tuptakip.user_permission_overrides (created by TupTakipDatabase.UpgradeSql).
internal sealed class PostgresPermissionOverrideStore : IPermissionOverrideStore
{
    public Dictionary<int, Dictionary<string, bool>> LoadAll()
    {
        Dictionary<int, Dictionary<string, bool>> result = [];
        using NpgsqlConnection connection = new(TupTakipDatabase.ConnectionString);
        connection.Open();
        using NpgsqlCommand command = new("SELECT user_id, permission, granted FROM user_permission_overrides", connection);
        using NpgsqlDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            int user = reader.GetInt32(0);
            if (!result.TryGetValue(user, out Dictionary<string, bool>? map))
            {
                result[user] = map = [];
            }

            map[reader.GetString(1)] = reader.GetBoolean(2);
        }

        return result;
    }

    public void Replace(int userId, IReadOnlyDictionary<string, bool> overrides, string actor)
    {
        using NpgsqlConnection connection = new(TupTakipDatabase.ConnectionString);
        connection.Open();
        using DbTransaction transaction = connection.BeginTransaction();
        using (NpgsqlCommand delete = new("DELETE FROM user_permission_overrides WHERE user_id = @u", connection, (NpgsqlTransaction)transaction))
        {
            delete.Parameters.AddWithValue("u", userId);
            delete.ExecuteNonQuery();
        }

        foreach ((string key, bool granted) in overrides)
        {
            using NpgsqlCommand insert = new(
                "INSERT INTO user_permission_overrides (user_id, permission, granted, updated_at, updated_by) VALUES (@u, @p, @g, now(), @a)",
                connection, (NpgsqlTransaction)transaction);
            insert.Parameters.AddWithValue("u", userId);
            insert.Parameters.AddWithValue("p", key);
            insert.Parameters.AddWithValue("g", granted);
            insert.Parameters.AddWithValue("a", actor.Length > 60 ? actor[..60] : actor);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}

// In-memory copy of the overrides; reloaded after every change and at most a minute after an outside edit.
internal static class PermissionOverrides
{
    private static readonly Lock Gate = new();
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);
    private static IPermissionOverrideStore _store = new PostgresPermissionOverrideStore();
    private static Dictionary<int, Dictionary<string, bool>>? _cache;
    private static DateTime _loadedUtc;

    internal static void UseStore(IPermissionOverrideStore store)
    {
        lock (Gate)
        {
            _store = store;
            _cache = null;
        }
    }

    internal static IReadOnlyDictionary<string, bool> For(int userId)
    {
        lock (Gate)
        {
            if (_cache is null || DateTime.UtcNow - _loadedUtc > MaxAge)
            {
                try
                {
                    _cache = _store.LoadAll();
                    _loadedUtc = DateTime.UtcNow;
                }
                catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or TimeoutException)
                {
                    AppLog.Write("Yetki istisnaları okunamadı: " + exception.Message);
                    if (_cache is null)
                    {
                        // Without the overrides only the stricter of role defaults/denials is unknown; refuse rather than guess.
                        throw;
                    }
                }
            }

            return _cache.TryGetValue(userId, out Dictionary<string, bool>? map) ? map : new Dictionary<string, bool>();
        }
    }

    internal static void Replace(int userId, IReadOnlyDictionary<string, bool> overrides, string actor)
    {
        lock (Gate)
        {
            _store.Replace(userId, overrides, actor);
            _cache = null;
        }
    }
}
