using ArsanTupTakip.Api;
using ArsanTupTakip.Services;

namespace ArsanTupTakip.Tests;

internal static class CylinderTripTrackingTests
{
    internal static void Run()
    {
        ReconcileFlagsMissingAndUnexpected();
        CloseWithoutReasonsIsRefused();
        CloseWithReasonsIsAllowed();
        ReasonValidation();
        ManagerNoticeHasNoPhone();
        LoadDuplicateMessagesDistinguishDelivered();
        Console.WriteLine("PASS: sefer kontrolü, gerekçesiz kapanış reddi, gerekçeli kapanış, gerekçe doğrulaması, yönetici bildirimi (telefonsuz) ve yükleme tekrar uyarısı.");
    }

    private static void LoadDuplicateMessagesDistinguishDelivered()
    {
        Assert(CylinderTripTracking.LoadDuplicateMessage("X1", null, null).Contains("zaten araçta"),
            "Araçtaki tüp 'zaten araçta' demeli.");
        string delivered = CylinderTripTracking.LoadDuplicateMessage("X2", DateTime.Now, null);
        Assert(delivered.Contains("müşteriye teslim", StringComparison.OrdinalIgnoreCase)
            && delivered.Contains("boş alım", StringComparison.OrdinalIgnoreCase)
            && !delivered.Contains("zaten sefer listesinde", StringComparison.Ordinal),
            "Teslim edilmiş tüp sefer listesinde sayılmamalı; boş alıma yönlendirmeli.");
        Assert(CylinderTripTracking.LoadDuplicateMessage("X3", null, DateTime.Now).Contains("depoya dönüş", StringComparison.OrdinalIgnoreCase),
            "Dönüşü okutulmuş tüp ayrı uyarı almalı.");
    }

    private static void ReconcileFlagsMissingAndUnexpected()
    {
        List<TripCylinderSnapshot> loaded =
        [
            new(1, "A1", "Oksijen", "01 ABC 1", null, null),
            new(2, "A2", "Oksijen", "01 ABC 1", null, null),
            new(3, "A3", "Argon", "01 ABC 1", DeliveredAt: DateTime.Now, null)
        ];
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A1"] = 1,
            ["A2"] = 2,
            ["A9"] = 9
        };
        TripReconcileResult result = CylinderTripTracking.Reconcile(loaded, ["A1", "A9"], map, "01 ABC 1");
        Assert(result.NotDeliveredNotReturned.Contains("A2"), "Undelivered and not returned cylinder is flagged.");
        Assert(result.UnexpectedReturnCodes.Contains("A9"), "Return scan outside load list is unexpected.");
        Assert(result.ShortagesByGas.Any(x => x.GasType == "Oksijen" && x.Count == 1), "Gas type shortage is counted.");
        Assert(result.UnexplainedMissing.SequenceEqual(["A2"]) && result.AlertText.Contains("Gerekçe seçilmemiş 1 tüp", StringComparison.Ordinal),
            "Gerekçesiz eksik tüp uyarıda ayrıca yazılmalı.");
    }

    private static void CloseWithoutReasonsIsRefused()
    {
        List<TripCylinderSnapshot> rows =
        [
            new(1, "B2", "Oksijen", "34 XY 2", null, null),
            new(2, "B1", "Argon", "34 XY 1", null, null, MissingReason: "kayip"),
            new(3, "B3", "Argon", "34 XY 1", null, null),
            new(4, "B4", "Argon", "34 XY 1", DeliveredAt: DateTime.Now, null),
            new(5, "B5", "Argon", "34 XY 1", null, ReturnedAt: DateTime.Now)
        ];

        TripReasonsRequiredException? refused = null;
        try
        {
            CylinderTripTracking.EnsureCanClose(true, rows);
        }
        catch (TripReasonsRequiredException exception)
        {
            refused = exception;
        }

        Assert(refused is not null, "Gerekçesi eksik tüp varken yeni sefer kapatılamamalı.");
        Assert(refused!.Serials.SequenceEqual(["B3", "B2"]), "Yalnızca teslim/dönüşü olmayan ve gerekçesi seçilmemiş tüpler (plaka sırasıyla) listelenmeli.");
        Assert(refused.Message.Contains("B3", StringComparison.Ordinal) && refused.Message.Contains("gerekçe", StringComparison.Ordinal), "Ret mesajı Türkçe ve tüpleri saymalı.");

        // Trips that were already open when reasons became mandatory close as before (no lock-in after the update).
        CylinderTripTracking.EnsureCanClose(false, rows);
        Assert(CylinderTripTracking.UnexplainedForClose(false, rows).Count == 0, "Eski açık seferlerde gerekçe zorunlu olmamalı.");
    }

    private static void CloseWithReasonsIsAllowed()
    {
        List<TripCylinderSnapshot> rows =
        [
            new(1, "C1", "Oksijen", "34 XY 1", null, null, MissingReason: "arac-icinde"),
            new(2, "C2", "Oksijen", "34 XY 1", null, null, MissingReason: "musteride-okutulmadi"),
            new(3, "C3", "Argon", "34 XY 1", DeliveredAt: DateTime.Now, null)
        ];
        CylinderTripTracking.EnsureCanClose(true, rows);
        Assert(CylinderTripTracking.UnexplainedForClose(true, rows).Count == 0, "Her eksik tüpün gerekçesi varsa sefer kapanabilmeli.");
        CylinderTripTracking.EnsureCanClose(true, [new(1, "D1", "Oksijen", "34 XY 1", DeliveredAt: DateTime.Now, null)]);
        CylinderTripTracking.EnsureCanClose(true, []);
    }

    private static void ReasonValidation()
    {
        Throws(() => TripMissingReasons.Validate("uydurma", "x"), "Bilinmeyen gerekçe reddedilmeli.");
        Throws(() => TripMissingReasons.Validate(null, null), "Gerekçe seçilmeden kayıt yapılamamalı.");
        Throws(() => TripMissingReasons.Validate("musteride-okutulmadi", null), "\"Müşteriye teslim edildi, okutulmadı\" müşteri veya açıklama istemeli.");
        Throws(() => TripMissingReasons.Validate("kayip", "  "), "Açıklama zorunlu gerekçe boş açıklamayla kaydedilmemeli.");
        Throws(() => TripMissingReasons.Validate("diger", new string('x', TripMissingReasons.MaxNoteLength + 1)), "Çok uzun açıklama reddedilmeli.");

        (TripMissingReason customer, string? note) = TripMissingReasons.Validate("MUSTERIDE-OKUTULMADI", "kapıda bırakıldı", "Arsan Gaz Ltd");
        Assert(customer.AsksCustomer && note == "Müşteri: Arsan Gaz Ltd · kapıda bırakıldı", "Seçilen müşterinin adı açıklamanın başına yazılmalı.");
        Assert(TripMissingReasons.Validate("musteride-okutulmadi", null, "Arsan Gaz Ltd").Note == "Müşteri: Arsan Gaz Ltd", "Müşteri seçildiyse açıklama zorunlu olmamalı.");
        Assert(TripMissingReasons.Validate("arac-icinde", null).Note is null, "Açıklaması isteğe bağlı gerekçe açıklamasız kaydedilmeli.");
        Assert(TripMissingReasons.Validate("kayip", "x", "Yok sayılır").Note == "x", "Müşteri sormayan gerekçede müşteri adı eklenmemeli.");
        Assert(TripMissingReasons.All.Select(r => r.Key).Distinct().Count() == TripMissingReasons.All.Count, "Gerekçe anahtarları benzersiz olmalı.");
    }

    private static void ManagerNoticeHasNoPhone()
    {
        TripReconcileResult reconcile = new(["E1", "E2"], [], [], ["E1", "E2"]);
        TripCloseResult closed = new(12, reconcile, "kapandı", "Sefer 14.10.2026 09:00", "Mobil (Saha telefonu)",
        [
            new("E1", "Oksijen", "34 ABC 12", "Ali Sürücü", "musteride-okutulmadi", TripMissingReasons.Label("musteride-okutulmadi"),
                "Müşteri: Örnek Medikal · arayın 0532 123 45 67 veya +90 (212) 555-12-34", "Mobil (Saha telefonu)"),
            new("E2", "Argon", "34 ABC 12", null, null, null, null, null)
        ]);

        string text = closed.ManagerNotice!;
        foreach (string expected in new[] { "Sefer 14.10.2026 09:00", "#12", "34 ABC 12", "Ali Sürücü", "E1", "E2", "Müşteriye teslim edildi, okutulmadı", "Örnek Medikal", "Gerekçe: YOK", "Mobil (Saha telefonu)" })
        {
            Assert(text.Contains(expected, StringComparison.Ordinal), $"Yönetici bildirimi \"{expected}\" içermeli.");
        }

        Assert(!text.Contains("0532", StringComparison.Ordinal) && !text.Contains("555-12-34", StringComparison.Ordinal) && !text.Contains("123 45 67", StringComparison.Ordinal),
            "Yönetici bildiriminde telefon numarası olmamalı.");
        Assert(text.Contains("[telefon gizlendi]", StringComparison.Ordinal), "Açıklamadaki telefon gizlendiği belli olmalı.");
        Assert(new TripCloseResult(13, new([], [], [], []), "kapandı").ManagerNotice is null, "Eksik tüp yoksa bildirim gönderilmemeli.");

        ManagerNotices.Capture = true;
        try
        {
            while (ManagerNotices.Captured.TryDequeue(out _))
            {
            }

            CylinderTripEndpoints.NotifyManagers(closed);
            CylinderTripEndpoints.NotifyManagers(new TripCloseResult(13, new([], [], [], []), "kapandı"));
            Assert(ManagerNotices.Captured.Count == 1 && ManagerNotices.Captured.TryPeek(out string? sent) && sent == text,
                "Kapanışta yalnızca eksik tüp varsa yöneticiye tek bildirim gitmeli.");
        }
        finally
        {
            ManagerNotices.Capture = false;
        }

        string directory = Path.Combine(Path.GetTempPath(), "tuptakip-notices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string? previous = Environment.GetEnvironmentVariable(ManagerNotices.DirectoryVariable);
        Environment.SetEnvironmentVariable(ManagerNotices.DirectoryVariable, directory);
        try
        {
            Assert(ManagerNotices.Send("sefer-kayip-12", text), "Bildirim kutusuna yazılabilmeli.");
            string[] files = Directory.GetFiles(directory);
            Assert(files.Length == 1 && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(files[0]), "^[a-z0-9][a-z0-9-]{0,80}\\.txt$"),
                "Bildirim dosya adı botun kabul ettiği biçimde olmalı (geçici dosya kalmamalı).");
            Assert(File.ReadAllText(files[0]) == text, "Bildirim metni aynen yazılmalı.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ManagerNotices.DirectoryVariable, previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Throws(Action action, string message)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
