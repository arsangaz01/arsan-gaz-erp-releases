namespace ArsanGazMobile.Services;

// Android bildirim zamanlayıcısının iOS karşılığı; şimdilik no-op.
public static class FollowUpReminder
{
    public static void Schedule(object? context)
    {
    }

    public static Task CheckAsync(object context, bool onceToday = false) => Task.CompletedTask;
}
