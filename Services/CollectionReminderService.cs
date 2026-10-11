using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed record CollectionReminderInput(
    string CustomerName,
    decimal Amount,
    string Currency,
    DateTime DueDate,
    int RemindDaysBefore,
    bool RepeatMonthly,
    string? Note);

public sealed record CollectionReminderDigest(int Count, string Text);

public sealed class CollectionReminderService
{
    public const int MaximumRemindDaysBefore = 30;
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly string[] Currencies = ["TRY", "USD", "EUR", "GBP"];

    private const string SchemaSql = """
CREATE TABLE IF NOT EXISTS CollectionReminders (
 Id INTEGER NOT NULL CONSTRAINT PK_CollectionReminders PRIMARY KEY AUTOINCREMENT,
 CustomerName TEXT NOT NULL,
 Amount TEXT NOT NULL DEFAULT '0',
 Currency TEXT NOT NULL DEFAULT 'TRY',
 DueDate TEXT NOT NULL,
 RemindDaysBefore INTEGER NOT NULL DEFAULT 1,
 RepeatMonthly INTEGER NOT NULL DEFAULT 0,
 Note TEXT NULL,
 Status TEXT NOT NULL DEFAULT 'Bekliyor',
 LastNotifiedOn TEXT NULL,
 CreatedAtUtc TEXT NOT NULL,
 CollectedAtUtc TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_CollectionReminders_Status_DueDate ON CollectionReminders(Status, DueDate);
""";

    public static async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using ArsanGazDbContext db = new();
        if (ArsanDatabase.IsPostgres)
        {
            await ArsanDbEf.EnsurePostgresSchemaAsync(db, DatabaseKind.Erp, cancellationToken);
            return;
        }

        await db.Database.ExecuteSqlRawAsync(SchemaSql, cancellationToken);
    }

    public async Task<List<CollectionReminder>> ListAsync(bool includeClosed, CancellationToken cancellationToken = default)
    {
        await using ArsanGazDbContext db = new();
        IQueryable<CollectionReminder> query = db.CollectionReminders.AsNoTracking();
        if (!includeClosed)
        {
            query = query.Where(reminder => reminder.Status == CollectionReminder.PendingStatus);
        }

        List<CollectionReminder> reminders = await query.ToListAsync(cancellationToken);
        return reminders
            .OrderBy(reminder => reminder.Status == CollectionReminder.PendingStatus ? 0 : 1)
            .ThenBy(reminder => reminder.DueDate)
            .ThenBy(reminder => reminder.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<CollectionReminder> AddAsync(CollectionReminderInput input, CancellationToken cancellationToken = default)
    {
        Validate(input);
        CollectionReminder reminder = new()
        {
            CustomerName = input.CustomerName.Trim(),
            Amount = input.Amount,
            Currency = input.Currency.Trim().ToUpperInvariant(),
            DueDate = input.DueDate.Date,
            RemindDaysBefore = input.RemindDaysBefore,
            RepeatMonthly = input.RepeatMonthly,
            Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim()
        };

        await using ArsanGazDbContext db = new();
        db.CollectionReminders.Add(reminder);
        await db.SaveChangesAsync(cancellationToken);
        return reminder;
    }

    public static void Validate(CollectionReminderInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.CustomerName) || input.CustomerName.Trim().Length > 200)
        {
            throw new ArgumentException("Müşteri adı 1-200 karakter olmalıdır.");
        }

        if (input.Amount < 0)
        {
            throw new ArgumentException("Tutar negatif olamaz.");
        }

        if (!Currencies.Contains(input.Currency?.Trim().ToUpperInvariant()))
        {
            throw new ArgumentException("Para birimi TRY, USD, EUR veya GBP olmalıdır.");
        }

        if (input.RemindDaysBefore is < 0 or > MaximumRemindDaysBefore)
        {
            throw new ArgumentException($"Hatırlatma 0-{MaximumRemindDaysBefore} gün önce olmalıdır.");
        }

        if (input.Note is { Length: > 1000 })
        {
            throw new ArgumentException("Not en fazla 1.000 karakter olabilir.");
        }
    }

    public async Task<string> MarkCollectedAsync(int id, CancellationToken cancellationToken = default)
    {
        await using ArsanGazDbContext db = new();
        CollectionReminder reminder = await FindPendingAsync(db, id, cancellationToken);
        reminder.Status = CollectionReminder.CollectedStatus;
        reminder.CollectedAtUtc = DateTime.UtcNow;

        string message = $"{reminder.CustomerName} tahsilatı kapatıldı.";
        if (reminder.RepeatMonthly)
        {
            CollectionReminder next = new()
            {
                CustomerName = reminder.CustomerName,
                Amount = reminder.Amount,
                Currency = reminder.Currency,
                DueDate = NextMonthlyDueDate(reminder.DueDate),
                RemindDaysBefore = reminder.RemindDaysBefore,
                RepeatMonthly = true,
                Note = reminder.Note
            };
            db.CollectionReminders.Add(next);
            message += $" Aylık tekrar nedeniyle sonraki ödeme günü {next.DueDate:dd.MM.yyyy} olarak eklendi.";
        }

        await db.SaveChangesAsync(cancellationToken);
        return message;
    }

    public async Task PostponeAsync(int id, int days, CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > 365)
        {
            throw new ArgumentOutOfRangeException(nameof(days), "Erteleme 1-365 gün olmalıdır.");
        }

        await using ArsanGazDbContext db = new();
        CollectionReminder reminder = await FindPendingAsync(db, id, cancellationToken);
        DateTime baseDate = reminder.DueDate.Date < DateTime.Today ? DateTime.Today : reminder.DueDate.Date;
        reminder.DueDate = baseDate.AddDays(days);
        reminder.LastNotifiedOn = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CancelAsync(int id, CancellationToken cancellationToken = default)
    {
        await using ArsanGazDbContext db = new();
        CollectionReminder reminder = await FindPendingAsync(db, id, cancellationToken);
        reminder.Status = CollectionReminder.CancelledStatus;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<CollectionReminderDigest?> PrepareDailyDigestAsync(DateTime today, CancellationToken cancellationToken = default)
    {
        today = today.Date;
        await using ArsanGazDbContext db = new();
        List<CollectionReminder> pending = await db.CollectionReminders
            .Where(reminder => reminder.Status == CollectionReminder.PendingStatus)
            .ToListAsync(cancellationToken);
        List<CollectionReminder> due = pending
            .Where(reminder => IsDue(reminder, today) && reminder.LastNotifiedOn?.Date != today)
            .OrderBy(reminder => reminder.DueDate)
            .ToList();
        if (due.Count == 0)
        {
            return null;
        }

        foreach (CollectionReminder reminder in due)
        {
            reminder.LastNotifiedOn = today;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new CollectionReminderDigest(due.Count, BuildDigest(due, today));
    }

    public static bool IsDue(CollectionReminder reminder, DateTime today) =>
        reminder.Status == CollectionReminder.PendingStatus &&
        today.Date >= reminder.DueDate.Date.AddDays(-reminder.RemindDaysBefore);

    public static DateTime NextMonthlyDueDate(DateTime dueDate) => dueDate.Date.AddMonths(1);

    public static string Describe(CollectionReminder reminder, DateTime today)
    {
        int days = (reminder.DueDate.Date - today.Date).Days;
        string timing = days switch
        {
            0 => "bugün",
            > 0 => $"{days} gün kaldı",
            _ => $"{-days} gün gecikti"
        };
        string note = string.IsNullOrWhiteSpace(reminder.Note) ? string.Empty : " · " + reminder.Note;
        return $"{reminder.CustomerName} · {FormatAmount(reminder.Amount, reminder.Currency)} · " +
               $"ödeme günü {reminder.DueDate:dd.MM.yyyy} ({timing}){note}";
    }

    public static string BuildDigest(IReadOnlyList<CollectionReminder> reminders, DateTime today)
    {
        StringBuilder text = new();
        text.AppendLine($"Tahsilat hatırlatması · {today:dd.MM.yyyy}");
        foreach (CollectionReminder reminder in reminders)
        {
            text.AppendLine("• " + Describe(reminder, today));
        }

        foreach (IGrouping<string, CollectionReminder> currency in reminders.GroupBy(reminder => reminder.Currency))
        {
            text.AppendLine($"Toplam {currency.Key}: {FormatAmount(currency.Sum(reminder => reminder.Amount), currency.Key)}");
        }

        text.Append("Tahsil edildiyse Operasyon Merkezi > Tahsilat hatırlatmaları ekranından kapatın.");
        return text.ToString();
    }

    public static string FormatAmount(decimal amount, string currency) =>
        amount.ToString("N2", TurkishCulture) + " " + currency;

    public static bool TryParseAmount(string text, out decimal amount) =>
        decimal.TryParse(text?.Trim(), NumberStyles.Number, TurkishCulture, out amount) && amount >= 0;

    public static bool TryQueueTelegram(string report, out string? detail)
    {
        long? chatId;
        try
        {
            chatId = AgentChatService.GetTelegramNotificationChatId();
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException)
        {
            detail = exception.Message;
            return false;
        }

        if (chatId is null)
        {
            detail = "AgentWatcher'da Telegram sohbeti yapılandırılmamış.";
            return false;
        }

        string outbox = Path.Combine(AgentWatcherPaths.DataRoot, "TelegramOutbox");
        Directory.CreateDirectory(outbox);
        string id = "collection-" + Guid.NewGuid().ToString("N");
        string destination = Path.Combine(outbox, id + ".json");
        string temporary = destination + ".tmp";
        DateTime now = DateTime.UtcNow;
        var item = new
        {
            Id = id,
            Kind = "collection-reminder",
            TaskId = id,
            ChatId = chatId.Value,
            Report = report,
            CreatedUtc = now,
            NextAttemptUtc = now,
            Attempts = 0,
            DeliveredChunkCount = 0,
            Receipts = Array.Empty<object>(),
            LastError = (string?)null
        };
        File.WriteAllText(temporary, JsonSerializer.Serialize(item, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, destination);
        detail = null;
        return true;
    }

    private static async Task<CollectionReminder> FindPendingAsync(ArsanGazDbContext db, int id, CancellationToken cancellationToken) =>
        await db.CollectionReminders.FirstOrDefaultAsync(
            reminder => reminder.Id == id && reminder.Status == CollectionReminder.PendingStatus,
            cancellationToken)
        ?? throw new InvalidOperationException("Bekleyen tahsilat hatırlatması bulunamadı.");
}
