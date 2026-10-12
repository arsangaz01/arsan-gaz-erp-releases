using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;

namespace ArsanGazMobile.Services;

// Daily 09:00 phone notification for CRM follow-ups that are due today or overdue.
public static class FollowUpReminder
{
    private const string ChannelId = "takip";
    private const int NotificationId = 4712;
    private const string LastShownKey = "followup_last_shown";

    public static void Schedule(Context context)
    {
        DateTime next = DateTime.Today.AddHours(9);
        if (next <= DateTime.Now)
        {
            next = next.AddDays(1);
        }

        long triggerMs = new DateTimeOffset(next).ToUnixTimeMilliseconds();
        AlarmManager alarms = (AlarmManager)context.GetSystemService(Context.AlarmService)!;
        alarms.SetInexactRepeating(AlarmType.RtcWakeup, triggerMs, AlarmManager.IntervalDay, CreateAlarmIntent(context));
    }

    public static async Task CheckAsync(Context context, bool onceToday)
    {
        string today = DateTime.Today.ToString("yyyy-MM-dd");
        if (onceToday && Preferences.Default.Get(LastShownKey, string.Empty) == today)
        {
            return;
        }

        if (!await ApiClient.Instance.IsSignedInAsync() || ApiClient.Instance.ServerUrl.Length == 0 || !ApiClient.Instance.Can("crm-goruntule"))
        {
            return;
        }

        List<CrmItem> due;
        try
        {
            due = await ApiClient.Instance.FollowUpsAsync(0);
        }
        catch (ApiException)
        {
            return;
        }

        Preferences.Default.Set(LastShownKey, today);
        if (due.Count == 0)
        {
            return;
        }

        string names = string.Join(", ", due.Take(3).Select(item => item.Name)) + (due.Count > 3 ? $" ve {due.Count - 3} firma daha" : string.Empty);
        Show(context, $"Bugün {due.Count} CRM takibi var", names);
    }

    private static void Show(Context context, string title, string text)
    {
        NotificationManager manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "CRM takip hatırlatmaları", NotificationImportance.Default));
        }

        Intent open = new(context, typeof(MainActivity));
        open.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        PendingIntent tap = PendingIntent.GetActivity(context, NotificationId, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        Notification notification = new NotificationCompat.Builder(context, ChannelId)
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetContentTitle(title)
            .SetContentText(text)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(text + "\nTakip sekmesinden açabilirsiniz."))
            .SetContentIntent(tap)
            .SetAutoCancel(true)
            .Build()!;
        manager.Notify(NotificationId, notification);
    }

    private static PendingIntent CreateAlarmIntent(Context context) =>
        PendingIntent.GetBroadcast(context, NotificationId, new Intent(context, typeof(FollowUpAlarmReceiver)),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
}

[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class FollowUpAlarmReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null)
        {
            return;
        }

        PendingResult? pending = GoAsync();
        Context appContext = context.ApplicationContext!;
        Task.Run(async () =>
        {
            try
            {
                await FollowUpReminder.CheckAsync(appContext, onceToday: true);
            }
            finally
            {
                pending?.Finish();
            }
        });
    }
}

[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter([Intent.ActionBootCompleted])]
public sealed class FollowUpBootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is not null && intent?.Action == Intent.ActionBootCompleted)
        {
            FollowUpReminder.Schedule(context);
        }
    }
}
