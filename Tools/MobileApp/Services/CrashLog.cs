namespace ArsanGazMobile.Services;

public static class CrashLog
{
    private static string FilePath => Path.Combine(FileSystem.AppDataDirectory, "last-crash.txt");

    public static void Write(object? error, string source)
    {
        try
        {
            string text = $"{DateTime.Now:dd.MM.yyyy HH:mm:ss} · {source} · {AppInfo.Current.VersionString} (build {AppInfo.Current.BuildString})\n{error}";
            File.WriteAllText(FilePath, text.Length > 6000 ? text[..6000] : text);
        }
        catch
        {
        }
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
                Ui.Note("Bu ekranın görüntüsünü Telegram'dan gönderin; hatayı buna göre düzelteceğim."),
                Ui.Secondary("Metni kopyala", async (_, _) => await Clipboard.Default.SetTextAsync(details)),
                text
            }
        };
        if (onContinue is not null)
        {
            layout.Children.Insert(2, Ui.Primary("Uygulamayı aç", (_, _) => onContinue()));
        }

        return new ContentPage { Title = title, Content = new ScrollView { Content = layout } };
    }
}
