using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.OS;
using AndroidX.Core.App;
using Location = Android.Locations.Location;

namespace ArsanTupTakip.Mobile.Services;

// Foreground service of the approach warning: it keeps receiving positions while the screen is off or another app
// (the map) is in front, shows a persistent "Sefer rotası izleniyor" notification and vibrates when a stop is near.
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeLocation)]
public sealed class ProximityService : Service, ILocationListener
{
    private const string StopAction = "com.arsantuptakip.mobil.PROXIMITY_STOP";
    private const string WatchChannel = "sefer-izleme";
    private const string AlertChannel = "sefer-yaklasma";
    private const int WatchNotificationId = 4101;
    private const long IntervalMs = 10_000;
    private const float MinMoveMeters = 15;
    private const float MaxAccuracyMeters = 150;
    private static readonly long[] Pattern = [0, 600, 300, 600, 300, 900];

    private LocationManager? _manager;

    public static void Start()
    {
        Context context = Android.App.Application.Context;
        Intent intent = new(context, typeof(ProximityService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }

    public static void Stop()
    {
        Context context = Android.App.Application.Context;
        context.StopService(new Intent(context, typeof(ProximityService)));
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == StopAction)
        {
            ProximityAlerts.Enabled = false;
            ProximityAlerts.Stop();
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        try
        {
            EnsureChannels();
            Notification watching = new NotificationCompat.Builder(this, WatchChannel)
                .SetSmallIcon(Resource.Mipmap.appicon)
                .SetContentTitle("Sefer rotası izleniyor")
                .SetContentText("Planlı müşteriye yaklaşınca titreşimle uyarılırsınız. Konumunuz sunucuya gönderilmez.")
                .SetOngoing(true)
                .SetPriority(NotificationCompat.PriorityLow)
                .SetContentIntent(OpenAppIntent())
                .AddAction(0, "Uyarıyı kapat", PendingIntent.GetService(this, 1, new Intent(this, typeof(ProximityService)).SetAction(StopAction), PendingFlags()))
                .Build()!;
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                StartForeground(WatchNotificationId, watching, ForegroundService.TypeLocation);
            }
            else
            {
                StartForeground(WatchNotificationId, watching);
            }

            StartUpdates();
        }
        catch (Exception exception)
        {
            CrashLog.Write(exception, "ProximityService.Start");
            StopSelf();
        }

        return StartCommandResult.NotSticky;
    }

    private void StartUpdates()
    {
        _manager ??= (LocationManager?)GetSystemService(LocationService);
        if (_manager is null)
        {
            return;
        }

        _manager.RemoveUpdates(this);
        foreach (string provider in new[] { LocationManager.GpsProvider, LocationManager.NetworkProvider })
        {
            if (_manager.AllProviders.Contains(provider))
            {
                _manager.RequestLocationUpdates(provider, IntervalMs, MinMoveMeters, this, Looper.MainLooper);
            }
        }
    }

    public void OnLocationChanged(Location location)
    {
        if (location.HasAccuracy && location.Accuracy > MaxAccuracyMeters)
        {
            return;
        }

        foreach ((ProximityTarget target, double distance) in ProximityAlerts.Check(location.Latitude, location.Longitude))
        {
            Warn(target, distance);
            ProximityAlerts.Raise(target, distance);
        }
    }

    private void Warn(ProximityTarget target, double distance)
    {
        try
        {
            Vibrate();
            Notification alert = new NotificationCompat.Builder(this, AlertChannel)
                .SetSmallIcon(Resource.Mipmap.appicon)
                .SetContentTitle($"{target.Name} yakında")
                .SetContentText($"Yaklaşık {Math.Round(distance / 10) * 10:0} m kaldı. Tüp teslimini unutmayın.")
                .SetPriority(NotificationCompat.PriorityHigh)
                .SetCategory(NotificationCompat.CategoryReminder)
                .SetVibrate(Pattern)
                .SetAutoCancel(true)
                .SetContentIntent(OpenAppIntent())
                .Build()!;
            NotificationManagerCompat.From(this).Notify(5000 + target.StopId, alert);
        }
        catch (Exception exception)
        {
            CrashLog.Write(exception, "ProximityService.Warn");
        }
    }

    private void Vibrate()
    {
        Vibrator? vibrator = OperatingSystem.IsAndroidVersionAtLeast(31)
            ? ((VibratorManager?)GetSystemService(VibratorManagerService))?.DefaultVibrator
            : (Vibrator?)GetSystemService(VibratorService);
        if (vibrator is null || !vibrator.HasVibrator)
        {
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            vibrator.Vibrate(VibrationEffect.CreateWaveform(Pattern, -1));
        }
        else
        {
#pragma warning disable CA1422
            vibrator.Vibrate(Pattern, -1);
#pragma warning restore CA1422
        }
    }

    private void EnsureChannels()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return;
        }

        NotificationManager manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(WatchChannel, "Sefer rotası izleme", NotificationImportance.Low)
        {
            Description = "Sefer sırasında yaklaşma uyarısı açıkken görünen kalıcı bildirim."
        });
        NotificationChannel alerts = new(AlertChannel, "Müşteriye yaklaşma uyarısı", NotificationImportance.High)
        {
            Description = "Planlı müşteriye yaklaşınca titreşimli uyarı."
        };
        alerts.EnableVibration(true);
        alerts.SetVibrationPattern(Pattern);
        manager.CreateNotificationChannel(alerts);
    }

    private PendingIntent OpenAppIntent()
    {
        Intent open = PackageManager!.GetLaunchIntentForPackage(PackageName!) ?? new Intent(this, typeof(MainActivity));
        open.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        return PendingIntent.GetActivity(this, 0, open, PendingFlags())!;
    }

    private static PendingIntentFlags PendingFlags() =>
        PendingIntentFlags.UpdateCurrent | (OperatingSystem.IsAndroidVersionAtLeast(23) ? PendingIntentFlags.Immutable : 0);

    public override void OnDestroy()
    {
        try
        {
            _manager?.RemoveUpdates(this);
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
            {
                StopForeground(StopForegroundFlags.Remove);
            }
        }
        catch (Exception exception)
        {
            CrashLog.Write(exception, "ProximityService.Destroy");
        }

        base.OnDestroy();
    }

    public void OnProviderDisabled(string provider)
    {
    }

    public void OnProviderEnabled(string provider)
    {
    }

    public void OnStatusChanged(string? provider, Availability status, Bundle? extras)
    {
    }
}
