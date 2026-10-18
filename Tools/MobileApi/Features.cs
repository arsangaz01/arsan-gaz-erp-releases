using System.Text.Encodings.Web;
using System.Text.Json;

namespace ArsanGazERP.MobileApi;

internal sealed record FeatureInfo(string Key, string Label, string Description, string[] Permissions, string[]? Needs = null);

// Optional features a Yönetici switches on or off for everyone (panel Yönetim > Özellikler). A switched-off feature
// takes its permissions out of every user's effective set, so its screens disappear and its endpoints are refused,
// while the per-user permission choices stay stored and come back unchanged when the feature is switched on again.
internal static class Features
{
    internal const string CustomerPortal = "musteri-portali";

    internal static readonly FeatureInfo[] Catalog =
    [
        // Permissions list empty on purpose: staff "Müşteri Talepleri" must keep working when the portal is off
        // (ERP already used staff-entered requests). Portal login is gated separately in WebPanelAuth / CustomerPortalEndpoints.
        new(CustomerPortal, "Müşteri portalı (müşteri girişi)",
            "Müşteriler kendi kullanıcı adı ve şifreleriyle arsantuptakip.com/musteri adresinden yalnızca kendi tüplerini görür, boş tüp alımı veya dolu tüp siparişi ister; talepler panelde Müşteri Talepleri sekmesine düşer. Müşteri hesapları Yönetim bölümünden açılır. Kapalıyken müşteri girişi olmaz; personel talepleri yine çalışır.",
            [])
    ];

    internal static readonly IReadOnlyDictionary<string, FeatureInfo> ByKey = Catalog.ToDictionary(f => f.Key);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Lock Gate = new();
    private static Dictionary<string, bool>? _memory;
    private static Dictionary<string, bool> _cache = [];
    private static (DateTime WriteUtc, long Length) _cacheStamp = (DateTime.MinValue, -1);

    internal static string FilePath => Path.Combine(MobileApiPaths.Root, "features.json");

    internal static void UseMemory(Dictionary<string, bool>? switches)
    {
        lock (Gate)
        {
            _memory = switches;
        }
    }

    internal static bool IsOn(string key) => IsOn(Current(), key);

    private static bool IsOn(Dictionary<string, bool> switches, string key) =>
        switches.TryGetValue(key, out bool on) && on
        && (!ByKey.TryGetValue(key, out FeatureInfo? feature) || (feature.Needs ?? []).All(n => switches.TryGetValue(n, out bool needOn) && needOn));

    internal static HashSet<string> DisabledPermissions()
    {
        Dictionary<string, bool> switches = Current();
        return [.. Catalog.Where(f => !IsOn(switches, f.Key)).SelectMany(f => f.Permissions)];
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
            string? missing = on
                ? (feature.Needs ?? []).Where(n => !(switches.TryGetValue(n, out bool v) && v)).Select(n => ByKey[n].Label).FirstOrDefault()
                : null;
            if (missing is not null)
            {
                return $"Önce \"{missing}\" özelliğini açın.";
            }

            string? dependent = on
                ? null
                : Catalog.Where(f => (f.Needs ?? []).Contains(key) && switches.TryGetValue(f.Key, out bool v) && v).Select(f => f.Label).FirstOrDefault();
            if (dependent is not null)
            {
                return $"Önce \"{dependent}\" özelliğini kapatın.";
            }

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

        MobileLog.Write($"Özellik {(on ? "açıldı" : "kapatıldı")}: {feature.Label} — {actor}, {ip}.");
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
                    MobileLog.Write("Özellik ayarları okunamadı: " + exception.Message);
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
