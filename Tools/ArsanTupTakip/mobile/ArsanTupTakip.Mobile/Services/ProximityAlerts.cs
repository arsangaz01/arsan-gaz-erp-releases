namespace ArsanTupTakip.Mobile.Services;

public sealed record ProximityTarget(int StopId, int CustomerId, string Name, double Latitude, double Longitude, int Meters);

// Approach warning of the trip plan: while a trip with located stops is open, a foreground service follows the phone's
// position and warns once per stop (vibration + notification) when it comes within the stop's distance. The position
// stays on the phone; nothing is sent to the server.
public static class ProximityAlerts
{
    private const string EnabledKey = "proximity-alerts-enabled";
    private static readonly Lock Gate = new();
    private static List<ProximityTarget> _targets = [];
    private static readonly HashSet<int> Alerted = [];
    private static int _tripId;

    // Raised on the main thread with the stop and the distance in metres.
    public static event Action<ProximityTarget, double>? Approached;

    public static bool Enabled
    {
        get => Preferences.Default.Get(EnabledKey, true);
        set => Preferences.Default.Set(EnabledKey, value);
    }

    public static bool Running { get; private set; }

    public static void SetTargets(int tripId, IEnumerable<ProximityTarget> targets)
    {
        lock (Gate)
        {
            if (tripId != _tripId)
            {
                Alerted.Clear();
                _tripId = tripId;
            }

            _targets = [.. targets];
        }
    }

    // Stops reached for the first time at this position; each stop warns only once per trip.
    public static List<(ProximityTarget Target, double Distance)> Check(double latitude, double longitude)
    {
        List<(ProximityTarget, double)> hits = [];
        lock (Gate)
        {
            foreach (ProximityTarget target in _targets)
            {
                if (Alerted.Contains(target.StopId))
                {
                    continue;
                }

                double distance = DistanceMeters(latitude, longitude, target.Latitude, target.Longitude);
                if (distance <= target.Meters)
                {
                    Alerted.Add(target.StopId);
                    hits.Add((target, distance));
                }
            }
        }

        return hits;
    }

    public static void Raise(ProximityTarget target, double distance) =>
        MainThread.BeginInvokeOnMainThread(() => Approached?.Invoke(target, distance));

    public static double DistanceMeters(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371000;
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLng = (lng2 - lng1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    // Asks for location (and on Android 13+ notification) permission when needed and starts the service.
    // Returns why it could not start, or null.
    public static async Task<string?> StartAsync(bool mayAsk)
    {
        PermissionStatus location = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (location != PermissionStatus.Granted && mayAsk)
        {
            location = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        }

        if (location != PermissionStatus.Granted)
        {
            Stop();
            return "Konum izni verilmediği için yaklaşma uyarısı çalışmıyor. Telefon Ayarlar > Uygulamalar > ArsanTüpTakip > İzinler'den konuma \"Uygulama kullanılırken\" izni verin.";
        }

        if (mayAsk && OperatingSystem.IsAndroidVersionAtLeast(33)
            && await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
        {
            await Permissions.RequestAsync<Permissions.PostNotifications>();
        }

        try
        {
            ProximityService.Start();
            Running = true;
            return null;
        }
        catch (Exception exception)
        {
            CrashLog.Write(exception, "ProximityAlerts.Start");
            Running = false;
            return "Yaklaşma uyarısı başlatılamadı: " + exception.Message;
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _targets = [];
        }

        if (Running)
        {
            Running = false;
            try
            {
                ProximityService.Stop();
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "ProximityAlerts.Stop");
            }
        }
    }
}
