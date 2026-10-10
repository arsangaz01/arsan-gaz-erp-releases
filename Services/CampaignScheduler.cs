using System.Globalization;
using ArsanGazERP.Models;

namespace ArsanGazERP.Services;

public sealed class CampaignScheduler
{
    private readonly CampaignService service;
    private readonly TimeSpan interval;
    private readonly Action<Exception> reportFailure;

    public CampaignScheduler(CampaignService service, Action<Exception> reportFailure, TimeSpan? interval = null)
    {
        this.service = service;
        this.reportFailure = reportFailure;
        this.interval = interval ?? TimeSpan.FromSeconds(10);
        if (this.interval < TimeSpan.FromSeconds(1) || this.interval > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(interval));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(interval);
        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await service.RefreshExecutionStateAsync(cancellationToken);
                    await service.DispatchOneAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    try
                    {
                        reportFailure(exception);
                    }
                    catch (Exception reportingException)
                    {
                        System.Diagnostics.Trace.TraceError(
                            $"Campaign scheduler failure reporter failed while reporting {exception.GetType().Name}: {reportingException.GetType().Name}: {reportingException.Message}");
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }
}

public static class CampaignWindowSchedule
{
    public static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Length > 128)
            throw new ArgumentException("A valid persisted timezone ID is required.", nameof(timeZoneId));
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new InvalidOperationException($"Campaign timezone '{timeZoneId}' is unavailable on this device.", exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new InvalidOperationException($"Campaign timezone '{timeZoneId}' is invalid on this device.", exception);
        }
    }

    public static void ValidateWindows(IReadOnlyList<CampaignDailyWindow> windows)
    {
        if (windows is null || windows.Count is < 1 or > 8)
            throw new ArgumentException("Define between one and eight local daily delivery windows.", nameof(windows));
        foreach (CampaignDailyWindow window in windows)
        {
            if (window is null || window.Start == window.End)
                throw new ArgumentException("A delivery window must have distinct local start and end times.", nameof(windows));
        }
    }

    public static bool IsWithinWindow(TimeZoneInfo timeZone, IReadOnlyList<CampaignDailyWindow> windows, DateTimeOffset instantUtc)
    {
        DateTime local = TimeZoneInfo.ConvertTime(instantUtc.ToUniversalTime(), timeZone).DateTime;
        TimeOnly now = TimeOnly.FromDateTime(local);
        foreach (CampaignDailyWindow window in windows)
        {
            if (window.Start < window.End && now >= window.Start && now < window.End)
                return true;
            if (window.Start > window.End && (now >= window.Start || now < window.End))
                return true;
        }
        return false;
    }

    public static DateTimeOffset? GetNextWindowStartUtc(
        TimeZoneInfo timeZone,
        IReadOnlyList<CampaignDailyWindow> windows,
        DateTimeOffset afterUtc)
    {
        DateTimeOffset after = afterUtc.ToUniversalTime();
        if (IsWithinWindow(timeZone, windows, after))
            return after;
        DateTime localNow = TimeZoneInfo.ConvertTime(after, timeZone).DateTime;
        DateTime firstDate = localNow.Date.AddDays(-1);
        List<DateTimeOffset> openings = [];
        for (int day = 0; day <= 9; day++)
        {
            DateTime date = firstDate.AddDays(day);
            foreach (CampaignDailyWindow window in windows)
            {
                DateTime localStart = date.Add(window.Start.ToTimeSpan());
                DateTimeOffset candidate;
                try
                {
                    candidate = ResolveLocalBoundary(timeZone, DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified));
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                if (candidate > after)
                    openings.Add(candidate);
            }
        }
        return openings.Count == 0 ? null : openings.Min();
    }

    public static DateTimeOffset ResolveStartInstant(TimeZoneInfo timeZone, DateTime localStart, TimeSpan? ambiguousOffset = null)
    {
        DateTime local = DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local))
            throw new ArgumentException("The selected local start time falls in a daylight-saving gap.", nameof(localStart));
        if (timeZone.IsAmbiguousTime(local))
        {
            if (ambiguousOffset is null || !timeZone.GetAmbiguousTimeOffsets(local).Contains(ambiguousOffset.Value))
                throw new ArgumentException("The selected local start time is ambiguous; choose which UTC offset to use.", nameof(localStart));
            return new DateTimeOffset(local, ambiguousOffset.Value).ToUniversalTime();
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }

    private static DateTimeOffset ResolveLocalBoundary(TimeZoneInfo timeZone, DateTime local)
    {
        if (timeZone.IsInvalidTime(local))
            throw new InvalidOperationException("A daily campaign window boundary falls in a daylight-saving gap.");
        if (timeZone.IsAmbiguousTime(local))
            throw new InvalidOperationException("A daily campaign window boundary is ambiguous and has no explicit UTC-offset choice.");
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }
}
