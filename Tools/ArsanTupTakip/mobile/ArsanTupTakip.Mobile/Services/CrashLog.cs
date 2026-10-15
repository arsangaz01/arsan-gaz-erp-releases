namespace ArsanTupTakip.Mobile.Services;

public static class CrashLog
{
    private static string FilePath => Path.Combine(FileSystem.AppDataDirectory, "last-crash.txt");

    // Sources that mean the app really crashed; anything else was caught and only shown on the next start.
    private static readonly HashSet<string> FatalSources = ["UnhandledException", "Android", "MainActivity"];

    public static void Write(object? error, string source)
    {
        if (!FatalSources.Contains(source) && error is Exception exception && IsConnectionError(exception))
        {
            return;
        }

        try
        {
            string text = $"{DateTime.Now:dd.MM.yyyy HH:mm:ss} · {source} · {AppInfo.Current.VersionString} (build {AppInfo.Current.BuildString})\n{error}";
            File.WriteAllText(FilePath, text.Length > 6000 ? text[..6000] : text);
        }
        catch
        {
        }
    }

    // No internet, DNS failure, timeout: the phone was offline, not a bug worth an error screen.
    public static bool IsConnectionError(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ServerUnreachableException or HttpRequestException or System.Net.WebException or TaskCanceledException
                or System.Net.Sockets.SocketException or Java.Net.UnknownHostException or Java.Net.SocketTimeoutException or Java.Net.ConnectException)
            {
                return true;
            }
        }

        return false;
    }

    public static string? TakeLast()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            string text = File.ReadAllText(FilePath);
            File.Delete(FilePath);
            return text;
        }
        catch
        {
            return null;
        }
    }

    public static ContentPage ErrorPage(string title, string details, Action? onContinue = null)
    {
        Editor text = new() { Text = details, IsReadOnly = true, FontSize = 12, AutoSize = EditorAutoSizeOption.TextChanges };
        VerticalStackLayout layout = new()
        {
            Padding = new Thickness(16),
            Spacing = 10,
            Children =
            {
                Ui.Title(title),
                Ui.Note("Bu ekranın görüntüsünü yöneticinize iletin."),
                Ui.Secondary("Metni kopyala", async (_, _) => await Clipboard.Default.SetTextAsync(details)),
                text
            }
        };
        if (onContinue is not null)
        {
            layout.Children.Insert(2, Ui.Primary("Uygulamayı aç", (_, _) => onContinue()));
        }

        ContentPage page = new() { Title = title, Content = new ScrollView { Content = layout } };
        Ui.ApplyPageChrome(page);
        return page;
    }
}
