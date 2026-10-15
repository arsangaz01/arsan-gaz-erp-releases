using ArsanGazERP.Services;

namespace ArsanGazERP.AgentWatcher.Tests;

internal static class CylinderTripTrackingTests
{
    internal static void Run()
    {
        ReconcileFlagsMissingAndUnexpected();
        ReconcileSeparatesExplainedMissing();
        MissingReasonValidation();
        OnlyFullCylindersAreLoaded();
        ReturnFullnessFollowsTheTrip();
        ManagerNoticeListsReasonsWithoutPhones();
        LoadDuplicateMessagesDistinguishDelivered();
    }

    private static void LoadDuplicateMessagesDistinguishDelivered()
    {
        Assert(CylinderTripTracking.LoadDuplicateMessage("X1", null, null).Contains("zaten araçta"),
            "Araçtaki tüp 'zaten araçta' demeli.");
        string delivered = CylinderTripTracking.LoadDuplicateMessage("X2", DateTime.Now, null);
        Assert(delivered.Contains("müşteriye teslim", StringComparison.OrdinalIgnoreCase)
            && delivered.Contains("boş alma", StringComparison.OrdinalIgnoreCase)
            && !delivered.Contains("zaten sefer listesinde", StringComparison.Ordinal),
            "Teslim edilmiş tüp sefer listesinde sayılmamalı; boş almaya yönlendirmeli.");
        Assert(CylinderTripTracking.LoadDuplicateMessage("X3", null, DateTime.Now).Contains("depoya dönüş", StringComparison.OrdinalIgnoreCase),
            "Dönüşü okutulmuş tüp ayrı uyarı almalı.");
    }

    private static void OnlyFullCylindersAreLoaded()
    {
        Assert(CylinderTripTracking.LoadRefusal(CylinderTracking.Warehouse, true) is null, "Full cylinder in the depot is loaded.");
        Assert(CylinderTripTracking.LoadRefusal(CylinderTracking.Warehouse, false)!.Contains("BOŞ"), "Empty cylinder is refused.");
        Assert(CylinderTripTracking.LoadRefusal(CylinderTracking.Warehouse, null) is null, "Unknown fullness is accepted (recorded as full).");
        Assert(CylinderTripTracking.LoadRefusal(CylinderTracking.Filling, false) is null, "A cylinder coming from the filling station is full.");
        Assert(CylinderTripTracking.LoadRefusal(CylinderTracking.Customer, true) is not null, "A cylinder registered at a customer is refused.");
    }

    private static void ReturnFullnessFollowsTheTrip()
    {
        Assert(CylinderTripTracking.ReturnFullness(onTrip: true, isPickup: false, delivered: false) == true, "Unsold full comes back full.");
        Assert(CylinderTripTracking.ReturnFullness(onTrip: true, isPickup: true, delivered: false) == false, "Empty from a customer comes back empty.");
        Assert(CylinderTripTracking.ReturnFullness(onTrip: false, isPickup: false, delivered: false) is null, "Cylinder outside the trip keeps the default rule.");
        Assert(CylinderTripTracking.ReturnFullness(onTrip: true, isPickup: false, delivered: true) is null, "Delivered cylinder keeps the default rule.");

        List<TripCylinderSnapshot> rows =
        [
            new(1, "F1", "Oksijen", "01 ABC 1", null, null),
            new(2, "P1", "Argon", "01 ABC 1", null, null, IsPickup: true)
        ];
        TripReconcileResult result = CylinderTripTracking.Reconcile(rows, [], new Dictionary<string, int>(), null);
        Assert(result.MissingEmpties.SequenceEqual(["P1"]), "Picked up empty that was not scanned back is missing.");
        Assert(result.AlertText.Contains("Müşteriden boş alınıp depoya okutulmayan 1 tüp: P1"), "Alert names the missing empty.");
        Assert(result.AlertText.Contains("Dolu yüklenip ne teslim ne de depoya geri okutulan 1 tüp: F1"), "Alert names the missing full.");
    }

    private static void ManagerNoticeListsReasonsWithoutPhones()
    {
        TripCloseResult empty = new(5, new TripReconcileResult([], [], [], []), "ok", "Sefer A");
        Assert(empty.ManagerNotice is null, "No notice when every cylinder was scanned.");

        TripCloseResult closed = new(7, new TripReconcileResult(["F1", "P1"], [], [], ["F1", "P1"]), "ok", "Sefer B", "Web panel (yonetici)",
        [
            new("F1", "Oksijen", "34 ABC 01", "Ali", false, null, "kayip", "Kayıp / bulunamadı", "depoda arandı", "Mobil (Tel)"),
            new("P1", "Argon", "34 ABC 01", null, true, "Beta Ltd", null, null, null, null)
        ]);
        string notice = closed.ManagerNotice!;
        Assert(notice.Contains("Sefer B (#7)") && notice.Contains("Web panel (yonetici)"), "Notice names the trip and who closed it.");
        Assert(notice.Contains("F1 (Oksijen, dolu yüklendi) · araç 34 ABC 01 · sürücü Ali"), "Notice lists plate and driver.");
        Assert(notice.Contains("Gerekçe: Kayıp / bulunamadı · Açıklama: depoda arandı"), "Notice carries reason and note.");
        Assert(notice.Contains("müşteriden boş alındı (Beta Ltd)") && notice.Contains("Gerekçe: YOK"), "Notice marks the pickup and a missing reason.");
    }

    private static void ReconcileSeparatesExplainedMissing()
    {
        List<TripCylinderSnapshot> loaded =
        [
            new(1, "B1", "Oksijen", "01 ABC 1", null, null, "kayip"),
            new(2, "B2", "Argon", "01 ABC 1", null, null),
            new(3, "B3", "Argon", "01 ABC 1", null, ReturnedAt: DateTime.Now, "arac-icinde")
        ];
        TripReconcileResult result = CylinderTripTracking.Reconcile(loaded, [], new Dictionary<string, int>(), null);
        Assert(result.NotDeliveredNotReturned.Count == 2, "Explained cylinders still count as missing until they are scanned.");
        Assert(result.UnexplainedMissing.SequenceEqual(["B2"]), "Only the cylinder without a reason is unexplained.");
        Assert(result.AlertText.Contains("Gerekçe seçilmemiş 1 tüp: B2"), "Alert names the unexplained cylinder.");

        TripReconcileResult allExplained = CylinderTripTracking.Reconcile(loaded.Take(1), [], new Dictionary<string, int>(), null);
        Assert(allExplained.HasAlerts && allExplained.UnexplainedMissing.Count == 0, "Explained missing cylinder keeps the alert but has no unexplained entry.");
        Assert(allExplained.AlertText.Contains("tamamına gerekçe seçilmiş"), "Alert says every missing cylinder is explained.");

        TripReconcileResult legacy = new(["X1"], [], [], ["X1"]);
        Assert(legacy.UnexplainedMissing.SequenceEqual(["X1"]), "Results built without reasons treat every missing cylinder as unexplained.");
    }

    private static void MissingReasonValidation()
    {
        (TripMissingReason reason, string? note) = TripMissingReasons.Validate(" ARAC-ICINDE ", "  ");
        Assert(reason.Key == "arac-icinde" && note is null, "Reason key is matched case-insensitively and a blank note becomes null.");
        Assert(Throws(() => TripMissingReasons.Validate("kayip", null)), "Lost cylinder needs a note.");
        Assert(Throws(() => TripMissingReasons.Validate("yok-boyle", "x")), "Unknown reason is rejected.");
        Assert(Throws(() => TripMissingReasons.Validate("diger", new string('a', TripMissingReasons.MaxNoteLength + 1))), "Too long note is rejected.");
        Assert(TripMissingReasons.Validate("musteride-okutulmadi", " ABC Ltd ").Note == "ABC Ltd", "Note is trimmed.");
        Assert(Throws(() => TripMissingReasons.Validate("musteride-okutulmadi", null)), "Delivered-not-scanned needs a customer or a note.");
        Assert(TripMissingReasons.Validate("musteride-okutulmadi", "kapıya bırakıldı", " Beta Ltd ").Note == "Müşteri: Beta Ltd · kapıya bırakıldı",
            "Picked customer is stored with the note.");
        Assert(TripMissingReasons.Validate("arac-icinde", null, "Beta Ltd").Note is null, "Customer is ignored for reasons that do not ask for one.");
        Assert(TripMissingReasons.Label("baska-araca") == "Başka araca aktarıldı", "Label is resolved from the key.");
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
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
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
