using System.Text.Encodings.Web;
using System.Text.Json;

namespace ArsanTupTakip.Api;

internal sealed record FeatureInfo(string Key, string Label, string Description, string[] Permissions);

// Optional features a Yönetici switches on or off for everyone (panel Yönetim > Özellikler). A switched-off feature
// takes its permissions out of every user's effective set, so its screens disappear and its endpoints are refused,
// while the per-user permission choices stay stored and come back unchanged when the feature is switched on again.
// Everything is off until switched on: the system then works exactly as before these features existed.
internal static class Features
{
    internal const string CustomerExchange = "musteri-degisim";
    internal const string Filling = "dolum";
    internal const string CustomerPortal = "musteri-portali";
    internal const string TripRoute = "sefer-rota";

    internal static readonly FeatureInfo[] Catalog =
    [
        new(CustomerExchange, "Müşteride dolu/boş değişimi ve bakiye",
            "Seferde \"Müşteriden boş alım\" okutması; her teslim ve boş alımdan sonra müşteriye verilen/alınan sayısı ve müşterideki güncel tüp bakiyesi; panelde sefer ayrıntısında müşteri değişimleri tablosu. Kapalıyken teslim eskisi gibi çalışır.",
            [Perm.TripPickup]),
        new(Filling, "Dolum kaydı",
            "Mobilde Dolum ekranı ve panelde Dolum sekmesi: doldurulan tüpler parti/lot numarasıyla okutulur, testi geçmiş tüp ayrıca onay ister. Kapalıyken dolum eskisi gibi tüp hareketinden yapılır.",
            [Perm.FillRecord]),
        new(TripRoute, "Sefer rotası ve yol tarifi",
            "Seferde araç başına müşteri sırası (durak listesi), Google Haritalar ile yol tarifi, arama ve sipariş zamanı gelen müşteri önerileri.",
            [Perm.TripRoute]),
        new(CustomerPortal, "Müşteri portalı (müşteri girişi)",
            "Müşteriler kendi kullanıcı adı ve şifreleriyle arsantuptakip.com/tuptakip/musteri adresinden yalnızca kendi tüplerini görür, boş tüp alımı veya dolu tüp siparişi ister; talepler panelde Müşteri Talepleri sekmesine ve yöneticiye bildirim olarak düşer. Müşteri hesapları Yönetim bölümünden açılır. Kapalıyken hiçbir müşteri giriş yapamaz.",
            [Perm.CustomerRequests])
    ];

    internal static readonly IReadOnlyDictionary<string, FeatureInfo> ByKey = Catalog.ToDictionary(f => f.Key);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Lock Gate = new();
    private static Dictionary<string, bool>? _memory;
    private static Dictionary<string, bool> _cache = [];
    private static (DateTime WriteUtc, long Length) _cacheStamp = (DateTime.MinValue, -1);

    internal static string FilePath => Path.Combine(AppPaths.Root, "features.json");

    // Tests keep the switches in memory instead of the data folder.
    internal static void UseMemory(Dictionary<string, bool>? switches)
    {
        lock (Gate)
        {
            _memory = switches;
        }
    }

    internal static bool IsOn(string key) => Current().TryGetValue(key, out bool on) && on;

    // Permissions of the features that are off; Perm.Effective removes them from every user.
    internal static HashSet<string> DisabledPermissions()
    {
        Dictionary<string, bool> switches = Current();
        return [.. Catalog.Where(f => !(switches.TryGetValue(f.Key, out bool on) && on)).SelectMany(f => f.Permissions)];
    }

    internal static FeatureInfo? OwnerOf(string permission) => Catalog.FirstOrDefault(f => f.Permissions.Contains(permission));

    internal static string? Set(string key, bool on, string actor, string ip)
    {
        if (!ByKey.TryGetValue(key, out FeatureInfo? feature))
        {
            return "Bilinmeyen özellik.";
        }

        lock (Gate)
        {
            Dictionary<string, bool> switches = new(Current());
            switches[key] = on;
            if (_memory is not null)
            {
                _memory = switches;
            }
            else
            {
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(switches, Json));
                File.Move(temp, FilePath, overwrite: true);
                _cache = switches;
                _cacheStamp = FileStamp();
            }
        }

        AppLog.Write($"Özellik {(on ? "açıldı" : "kapatıldı")}: {feature.Label} — {actor}, {ip}.");
        WebPanelAudit.Write(actor, on ? "Özellik açıldı" : "Özellik kapatıldı", feature.Label, ip: ip);
        return null;
    }

    private static Dictionary<string, bool> Current()
    {
        lock (Gate)
        {
            if (_memory is not null)
            {
                return _memory;
            }

            (DateTime, long) stamp = FileStamp();
            if (stamp != _cacheStamp)
            {
                try
                {
                    _cache = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(FilePath), Json) ?? [] : [];
                    _cacheStamp = stamp;
                }
                catch (Exception exception) when (exception is IOException or JsonException)
                {
                    AppLog.Write("Özellik ayarları okunamadı: " + exception.Message);
                }
            }

            return _cache;
        }
    }

    private static (DateTime, long) FileStamp()
    {
        FileInfo info = new(FilePath);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, 0);
    }
}
