using System.Text.RegularExpressions;

namespace ArsanTupTakip.Mobile.Services;

// Turns the server's plain-text reports into cards: "Name: 12" lines become number tiles, "Heading:" lines captions,
// "- a · b · c" lines list cards, "■ customer: ..." lines group cards with their indented lines inside.
public static partial class ReportView
{
    private const int MaxItems = 200;

    // No dot in the name so "15.10.2026 14:05 · ..." movement lines are not read as counts.
    [GeneratedRegex(@"^(?<name>[\p{L}\d][^:·.]{1,60}):\s*(?<value>\d+)(?<rest>.*)$")]
    private static partial Regex CountLine();

    public static View Build(string text)
    {
        VerticalStackLayout root = new() { Spacing = 10 };
        List<View> tiles = [];
        VerticalStackLayout? group = null;
        int items = 0;

        void FlushTiles()
        {
            if (tiles.Count > 0)
            {
                root.Children.Add(Ui.TileGrid([.. tiles]));
                tiles = [];
            }
        }

        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0)
            {
                FlushTiles();
                group = null;
                continue;
            }

            bool indented = line.StartsWith("  ", StringComparison.Ordinal);
            string body = line.Trim();
            if (indented && group is not null)
            {
                group.Children.Add(Ui.Note(body.TrimStart('-', ' ')));
                continue;
            }

            string plain = body.StartsWith("- ", StringComparison.Ordinal) ? body[2..] : body;
            if (CountLine().Match(plain) is { Success: true } count)
            {
                string name = count.Groups["name"].Value.Trim();
                string rest = count.Groups["rest"].Value.Trim().Trim('(', ')');
                tiles.Add(Ui.StatTile(Icon(name), count.Groups["value"].Value, name, Tone(name, count.Groups["value"].Value), rest));
                continue;
            }

            FlushTiles();
            if (body.StartsWith('■'))
            {
                string[] head = body.TrimStart('■', ' ').Split(':', 2);
                group = new VerticalStackLayout
                {
                    Spacing = 4,
                    Children = { new Label { Text = "👤 " + head[0].Trim(), FontSize = 16, FontFamily = "OpenSansSemibold" } }
                };
                if (head.Length > 1)
                {
                    group.Children.Add(Chips(head[1].Trim()));
                }

                root.Children.Add(Ui.Card(group));
                continue;
            }

            group = null;
            if (body.StartsWith("- ", StringComparison.Ordinal))
            {
                if (++items <= MaxItems)
                {
                    root.Children.Add(Item(plain));
                }

                continue;
            }

            root.Children.Add(body.EndsWith(':')
                ? Ui.SectionCaption(body.TrimEnd(':').ToUpperInvariant())
                : new Label { Text = body, FontSize = 15, FontFamily = "OpenSansSemibold", LineBreakMode = LineBreakMode.WordWrap });
        }

        FlushTiles();
        if (items > MaxItems)
        {
            root.Children.Add(Ui.Note($"… ve {items - MaxItems} satır daha (ilk {MaxItems} gösterildi)."));
        }

        return root;
    }

    private static Border Item(string text)
    {
        string[] parts = text.Split(" · ", 2);
        Color tone = Warning(text) ? Ui.Warn : Ui.Accent;
        Grid row = new()
        {
            ColumnSpacing = 10,
            ColumnDefinitions = [new ColumnDefinition(new GridLength(5)), new ColumnDefinition(GridLength.Star)]
        };
        row.Add(new BoxView { Color = tone, CornerRadius = 3 }, 0, 0);
        VerticalStackLayout body = new()
        {
            Spacing = 2,
            Children = { new Label { Text = parts[0], FontSize = 15, FontFamily = "OpenSansSemibold", LineBreakMode = LineBreakMode.WordWrap } }
        };
        if (parts.Length > 1)
        {
            Label detail = Ui.Note(parts[1].Replace(" · ", "  •  "));
            if (tone == Ui.Warn)
            {
                detail.TextColor = Ui.Warn;
            }

            body.Children.Add(detail);
        }

        row.Add(body, 1, 0);
        Border card = Ui.Card(row);
        card.Padding = new Thickness(10, 10);
        return card;
    }

    private static Label Chips(string text) => new()
    {
        Text = text.Replace(" · ", "  •  "),
        FontSize = 13,
        TextColor = Warning(text) ? Ui.Warn : Ui.Muted,
        LineBreakMode = LineBreakMode.WordWrap
    };

    private static bool Warning(string text) =>
        text.Contains('⚠') || text.Contains("geçti", StringComparison.OrdinalIgnoreCase) || text.Contains("geçmiş", StringComparison.OrdinalIgnoreCase);

    private static string Icon(string name)
    {
        string n = name.ToLower(new System.Globalization.CultureInfo("tr-TR"));
        return n switch
        {
            _ when n.Contains("müşteri") && n.Contains("gün") => "⏳",
            _ when n.Contains("müşteri") => "👥",
            _ when n.Contains("depo") => "🏭",
            _ when n.Contains("araç") || n.Contains("sefer") || n.Contains("taşın") => "🚚",
            _ when n.Contains("dolum") => "⛽",
            _ when n.Contains("test") => "🧪",
            _ when n.Contains("hareket") => "🔄",
            _ when n.Contains("hurda") || n.Contains("kayıp") || n.Contains("pasif") => "🗑️",
            _ when n.Contains("bakım") || n.Contains("tamir") => "🔧",
            _ => "🛢️"
        };
    }

    private static Color Tone(string name, string value)
    {
        string n = name.ToLower(new System.Globalization.CultureInfo("tr-TR"));
        bool bad = n.Contains("geçmiş") || n.Contains("kayıp") || n.Contains("uzun");
        bool soon = n.Contains("dolacak") || n.Contains("yaklaşan");
        return value == "0" ? Ui.Accent : bad ? Ui.Bad : soon ? Ui.Warn : Ui.Accent;
    }
}
