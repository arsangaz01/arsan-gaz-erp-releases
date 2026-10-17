using System.Globalization;

namespace ArsanGazERP.MobileApi;

internal sealed record PanelPermissionInfo(string Key, string Group, string Label, bool AdminOnly = false);

// Everything a panel or mobile user can be allowed to do. Every /panel/api and /api endpoint names one of these keys
// (or is explicitly open to any signed-in user); the self-test fails when an endpoint has neither.
internal static class PanelPermissions
{
    internal const string Summary = "ozet";
    internal const string CylinderView = "tup-goruntule";
    internal const string CylinderRegister = "tup-kayit";
    internal const string CylinderEdit = "tup-duzenle";
    internal const string CylinderMove = "tup-hareket";
    internal const string CylinderDeliver = "tup-cikis";
    internal const string CylinderReturn = "tup-iade";
    internal const string CylinderAudit = "tup-sayim";
    internal const string FillRecord = "dolum-kaydi";
    internal const string StockCount = "depo-sayim";
    internal const string LabelPrint = "barkod-yazdir";
    internal const string MovementView = "hareket-goruntule";
    internal const string Reports = "rapor";
    internal const string Export = "disa-aktar";
    internal const string CustomerView = "musteri-goruntule";
    internal const string CustomerCreate = "musteri-ekle";
    internal const string CustomerStockTotals = "musteri-stok-toplam";
    internal const string CustomerRequests = "musteri-talepleri";
    internal const string CrmView = "crm-goruntule";
    internal const string CrmWrite = "crm-yaz";
    internal const string VehicleView = "arac-goruntule";
    internal const string VehicleManage = "arac-yonetimi";
    internal const string TripView = "sefer-goruntule";
    internal const string TripStart = "sefer-baslat";
    internal const string TripLoad = "sefer-yukle";
    internal const string TripDeliver = "sefer-teslim";
    internal const string TripClose = "sefer-kapat";
    internal const string TripRoute = "sefer-rota";
    internal const string TripPlan = "sefer-plani";
    internal const string CustomerLocation = "musteri-konum-kaydet";
    internal const string CampaignView = "kampanya-goruntule";
    internal const string CampaignWhatsApp = "kampanya-whatsapp";
    internal const string CampaignManage = "kampanya-yonet";
    internal const string Assistant = "asistan";
    internal const string CursorTask = "cursor-gorev";
    internal const string ManageUsers = "kullanici-yonetimi";
    internal const string ManageDevices = "cihaz-yonetimi";
    internal const string AuditLog = "denetim-kaydi";

    internal static readonly IReadOnlyList<PanelPermissionInfo> Catalog =
    [
        new(Summary, "Genel", "Özet ve durum ekranını görme"),
        new(CylinderView, "Tüpler", "Tüp listesi, tüp detayı ve barkod/QR ile sorgulama"),
        new(CylinderRegister, "Tüpler", "Yeni tüp kaydetme"),
        new(CylinderEdit, "Tüpler", "Tüp bilgilerini düzenleme ve pasife alma"),
        new(CylinderMove, "Tüpler", "Depo, dolum, araç, servis, hurda hareketi (dolu/boş dahil)"),
        new(CylinderDeliver, "Tüpler", "Müşteriye çıkış (tüpü müşteriye verme)"),
        new(CylinderReturn, "Tüpler", "Müşteriden iade (müşterideki tüpü geri alma)"),
        new(CylinderAudit, "Tüpler", "Müşteride tüp sayımı"),
        new(FillRecord, "Tüpler", "Dolum kaydı (lot/parti ile depoda dolu yapma)"),
        new(StockCount, "Tüpler", "Depo sayımı: telefonda ana depodaki tüpleri okutarak sayma ve panelde Sayım Raporları (yalnızca Yönetici ve Operatör)"),
        new(LabelPrint, "Tüpler", "Karekod/barkod etiketi yazdırma (panelde Karekod Yazdır sekmesi)"),
        new(MovementView, "Hareketler ve raporlar", "Hareket geçmişini görme"),
        new(Reports, "Hareketler ve raporlar", "Raporlar: uzun süre müşteride kalanlar, test tarihleri, sipariş tahmini"),
        new(Export, "Hareketler ve raporlar", "Excel/CSV'ye aktarma"),
        new(CustomerView, "Müşteriler", "Müşteri listesi ve arama"),
        new(CustomerCreate, "Müşteriler", "Yeni müşteri ekleme"),
        new(CustomerStockTotals, "Müşteriler", "Müşteri stoğunda tüm müşterilerin toplamı ve gaz/boyut tablosu"),
        new(CustomerRequests, "Müşteriler", "Müşteri talepleri: boş tüp alımı ve dolu tüp siparişi taleplerini görme, ekleme, tamamlandı/iptal işaretleme"),
        new(CrmView, "CRM", "CRM firmaları ve takip listesi"),
        new(CrmWrite, "CRM", "CRM notu ve takip tarihi girme"),
        new(VehicleView, "Araçlar", "Araçları ve araçtaki tüpleri görme"),
        new(VehicleManage, "Araçlar", "Araç ekleme, düzenleme, pasife alma"),
        new(TripView, "Seferler", "Seferleri ve araç yüklerini görme"),
        new(TripStart, "Seferler", "Sefer başlatma ve sefere araç ekleme"),
        new(TripLoad, "Seferler", "Araca tüp yükleme"),
        new(TripDeliver, "Seferler", "Seferde müşteriye teslim ve dönüş okutma"),
        new(TripClose, "Seferler", "Seferi kapatma"),
        new(TripRoute, "Seferler", "Sefer rotası: durak sırası, bugünkü sefer planım ve yol tarifi"),
        new(TripPlan, "Seferler", "Sefer planı: gün ve araç için uğranacak müşteriler, uyarı mesafesi ve müşteri konumu (panelde Sefer Planı sekmesi)"),
        new(CustomerLocation, "Seferler", "Seferde müşterinin yanındayken telefonun konumunu müşteri konumu olarak kaydetme"),
        new(CampaignView, "Kampanya", "Kampanya ve grupları görme"),
        new(CampaignWhatsApp, "Kampanya", "WhatsApp listesini alma (telefon numaraları), gönderildi/RET işaretleme"),
        new(CampaignManage, "Kampanya", "Grup oluşturma/silme, kampanya hazırlama, başlatma, durdurma, iptal"),
        new(Assistant, "Asistan", "ERP asistanı ile sohbet"),
        new(CursorTask, "Asistan", "Bilgisayardaki Cursor ajanına görev gönderme"),
        new(ManageUsers, "Yönetim", "Kullanıcı ekleme, yetki ve şifre yönetimi", AdminOnly: true),
        new(ManageDevices, "Yönetim", "Mobil cihaz oturumlarını görme ve kapatma"),
        new(AuditLog, "Yönetim", "Denetim kaydını görme")
    ];

    internal static readonly IReadOnlyList<string> All = Catalog.Select(p => p.Key).ToArray();

    internal static bool IsKnown(string? key) => key is not null && All.Contains(key);

    // Depot count stays with Yönetici and Operatör even when someone tries to grant it to a field or viewer account.
    internal static readonly IReadOnlyList<string> OperatorOnly = [StockCount];

    internal static bool IsAdminOnly(string key) => Catalog.Any(p => p.Key == key && p.AdminOnly);

    // Moving a cylinder onto a customer is a delivery, taking it off a customer a return; everything else is a plain move.
    internal static string ForMove(int fromStatus, int toStatus) =>
        toStatus == 3 ? CylinderDeliver : fromStatus == 3 ? CylinderReturn : CylinderMove;
}

// Roles are presets: a user gets the role's defaults, plus any extra permissions the administrator ticked, minus the ones
// unticked. Yönetici always has everything and cannot be narrowed.
internal static class PanelRoles
{
    internal const string Admin = "Yönetici";
    internal const string Operator = "Operatör";
    internal const string Driver = "Saha";
    internal const string Viewer = "İzleyici";
    internal static readonly string[] All = [Admin, Operator, Driver, Viewer];

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private static readonly string[] OperatorDefaults = PanelPermissions.All.Except(
    [
        PanelPermissions.ManageUsers, PanelPermissions.ManageDevices, PanelPermissions.AuditLog,
        PanelPermissions.CursorTask, PanelPermissions.CampaignManage, PanelPermissions.CampaignWhatsApp,
        PanelPermissions.CustomerStockTotals
    ]).ToArray();

    // TupTakip şoför menüsüyle aynı işler: sefer, plan, sayım, dolum (CRM/kampanya/asistan ofiste kalır).
    private static readonly string[] DriverDefaults =
    [
        PanelPermissions.Summary, PanelPermissions.CylinderView, PanelPermissions.CylinderMove, PanelPermissions.CylinderDeliver,
        PanelPermissions.CylinderReturn, PanelPermissions.CylinderAudit, PanelPermissions.MovementView, PanelPermissions.CustomerView,
        PanelPermissions.VehicleView, PanelPermissions.TripView, PanelPermissions.TripStart, PanelPermissions.TripLoad,
        PanelPermissions.TripDeliver, PanelPermissions.TripRoute, PanelPermissions.TripPlan, PanelPermissions.CustomerLocation,
        PanelPermissions.StockCount, PanelPermissions.FillRecord
    ];

    private static readonly string[] ViewerDefaults =
    [
        PanelPermissions.Summary, PanelPermissions.CylinderView, PanelPermissions.MovementView, PanelPermissions.Reports,
        PanelPermissions.Export, PanelPermissions.CustomerView, PanelPermissions.CrmView, PanelPermissions.VehicleView,
        PanelPermissions.TripView, PanelPermissions.CampaignView
    ];

    internal static IReadOnlyList<string> Defaults(string? role) => role switch
    {
        Admin => PanelPermissions.All,
        Operator => OperatorDefaults,
        Driver => DriverDefaults,
        Viewer => ViewerDefaults,
        _ => []
    };

    internal static IReadOnlyList<string> Effective(string? role, IEnumerable<string>? extra, IEnumerable<string>? removed)
    {
        if (role == Admin)
        {
            return PanelPermissions.All;
        }

        HashSet<string> set = [.. Defaults(role)];
        foreach (string key in extra ?? [])
        {
            if (PanelPermissions.IsKnown(key) && !PanelPermissions.IsAdminOnly(key))
            {
                set.Add(key);
            }
        }

        set.ExceptWith(removed ?? []);
        if (role != Operator)
        {
            set.ExceptWith(PanelPermissions.OperatorOnly);
        }

        return PanelPermissions.All.Where(set.Contains).ToArray();
    }

    internal static IReadOnlyList<string> Effective(WebPanelUser user) => Effective(user.Role, user.ExtraPermissions, user.RemovedPermissions);

    internal static bool Has(WebPanelUser user, string permission) => Effective(user).Contains(permission);

    // Splits the wanted permission set into the overrides stored on the user relative to the role defaults.
    internal static (List<string> Extra, List<string> Removed) Overrides(string role, IEnumerable<string> wanted)
    {
        HashSet<string> target = [.. wanted.Where(k => PanelPermissions.IsKnown(k) && !PanelPermissions.IsAdminOnly(k))];
        IReadOnlyList<string> defaults = Defaults(role);
        return (PanelPermissions.All.Where(k => target.Contains(k) && !defaults.Contains(k)).ToList(),
            PanelPermissions.All.Where(k => defaults.Contains(k) && !target.Contains(k) && !PanelPermissions.IsAdminOnly(k)).ToList());
    }

    internal static string Description(string role) => role switch
    {
        Admin => "Her şey; kullanıcı ve yetki yönetimi yalnızca bu roldedir. Daraltılamaz.",
        Operator => "Tüp, müşteri, CRM, araç ve sefer işlemlerinin tamamı, raporlar ve Excel. Kampanya yönetimi, Cursor görevi, cihaz yönetimi ve denetim kaydı hariç.",
        Driver => "Saha personeli/şoför: tüp okutma, hareket, müşteriye çıkış ve iade, sayım, araca yükleme, teslim ve dönüş okutma.",
        Viewer => "Yalnızca görüntüleme, raporlar ve Excel'e aktarma; kayıt değiştiremez.",
        _ => string.Empty
    };

    internal static string? Normalize(string? role)
    {
        string text = new((role ?? string.Empty).Trim().ToUpper(Turkish).Select(c => c switch
        {
            'İ' => 'I', 'Ö' => 'O', 'Ü' => 'U', 'Ç' => 'C', 'Ş' => 'S', 'Ğ' => 'G', _ => c
        }).ToArray());
        return text switch
        {
            "YONETICI" or "ADMIN" => Admin,
            "OPERATOR" => Operator,
            "SAHA" or "SOFOR" or "SURUCU" => Driver,
            "IZLEYICI" or "VIEWER" => Viewer,
            _ => null
        };
    }
}
