using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

// Tüp Bilgisi: read-only status card for one cylinder; nothing on this screen changes a record.
public sealed class CylinderInfoPage : ContentPage
{
    private readonly Entry _code = Ui.TextField("Barkod veya seri no");
    private readonly VerticalStackLayout _result = new() { Spacing = 12 };
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };

    public CylinderInfoPage(string? initialCode = null)
    {
        Title = "Tüp Bilgisi";
        Ui.ApplyPageChrome(this);
        _code.ReturnType = ReturnType.Search;
        _code.Completed += async (_, _) => await LookupAsync(_code.Text);
        if (!string.IsNullOrWhiteSpace(initialCode))
        {
            _code.Text = initialCode.Trim();
        }

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Note("Tüpün barkodunu veya QR kodunu okutun, galeriden bir fotoğraf seçin ya da seri numarasını yazın. Bu ekran yalnızca tüpün durumunu gösterir; kayıt değiştirmez."),
                    Ui.ActionRow(
                        SourceButton(Icons.PhotoCamera, "Kamera ile okut", async () => await ScanAsync()),
                        SourceButton(Icons.PhotoLibrary, "Galeriden seç", async () => await PickFromGalleryAsync())),
                    Ui.Section(
                        "Elle sorgula",
                        _code,
                        Ui.Primary("Sorgula", async (_, _) => await LookupAsync(_code.Text))),
                    _busy,
                    _result
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(initialCode))
        {
            Loaded += async (_, _) => await LookupAsync(initialCode);
        }
    }

    private static Border SourceButton(string glyph, string text, Func<Task> action)
    {
        Border tile = new()
        {
            Padding = new Thickness(12, 16),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            Background = new LinearGradientBrush(
                [new GradientStop(Color.FromArgb("#1E3A8A"), 0), new GradientStop(Color.FromArgb("#0D9488"), 1)],
                new Point(0, 0), new Point(1, 1)),
            Shadow = new Shadow { Brush = new SolidColorBrush(Colors.Black), Opacity = 0.2f, Radius = 10, Offset = new Point(0, 4) },
            Content = new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    Icons.Glyph(glyph, Colors.White, 30),
                    new Label { Text = text, TextColor = Colors.White, FontSize = 14, FontFamily = "OpenSansSemibold", HorizontalTextAlignment = TextAlignment.Center }
                }
            }
        };
        TapGestureRecognizer tap = new();
        tap.Tapped += async (_, _) =>
        {
            await tile.ScaleToAsync(0.95, 70);
            await tile.ScaleToAsync(1, 70);
            await action();
        };
        tile.GestureRecognizers.Add(tap);
        return tile;
    }

    private async Task ScanAsync()
    {
        try
        {
            if (await ScanPage.ScanAsync(Navigation) is string value)
            {
                await LookupAsync(value);
            }
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Kamera", exception.Message, "Tamam");
        }
    }

    private async Task PickFromGalleryAsync()
    {
        try
        {
            FileResult? file = await MediaPicker.Default.PickPhotoAsync(new MediaPickerOptions { Title = "Barkodlu fotoğraf" });
            if (file is null)
            {
                return;
            }

            IReadOnlyList<string> codes;
            _busy.IsRunning = _busy.IsVisible = true;
            try
            {
                await using Stream stream = await file.OpenReadAsync();
                codes = await ImageBarcodeReader.ReadAsync(stream);
            }
            finally
            {
                _busy.IsRunning = _busy.IsVisible = false;
            }

            string? chosen = codes.Count switch
            {
                0 => null,
                1 => codes[0],
                _ => await DisplayActionSheetAsync($"Fotoğrafta {codes.Count} kod bulundu", "Vazgeç", null, codes.ToArray())
            };
            if (codes.Count == 0)
            {
                await DisplayAlertAsync("Barkod bulunamadı",
                    "Fotoğrafta okunabilir barkod veya QR kod yok. Barkodun net, düz ve tamamen göründüğü bir fotoğraf deneyin.", "Tamam");
                return;
            }

            if (chosen is not null && codes.Contains(chosen))
            {
                await LookupAsync(chosen);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Galeri", exception.Message, "Tamam");
        }
    }

    private async Task LookupAsync(string? code)
    {
        string value = (code ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return;
        }

        _code.Text = value;
        _result.Children.Clear();
        await Ui.RunAsync(this, _busy, async () =>
        {
            try
            {
                Render(await ApiClient.Instance.CylinderCardAsync(value));
            }
            catch (ServerUnreachableException exception)
            {
                _result.Children.Add(Ui.Card(Ui.Note("Bağlantı yok; tüp bilgisi alınamadı. " + exception.Message)));
            }
            catch (ApiException exception) when (exception.StatusCode == 404)
            {
                _result.Children.Add(Ui.Card(new VerticalStackLayout
                {
                    Spacing = 6,
                    Children =
                    {
                        new Label { Text = "Tüp bulunamadı", FontSize = 17, FontFamily = "OpenSansSemibold" },
                        Ui.Note(exception.Message)
                    }
                }));
            }
        });
    }

    private void Render(CylinderCard card)
    {
        (Color start, Color end) = card.Status switch
        {
            1 => (Color.FromArgb("#1E3A8A"), Color.FromArgb("#2563EB")),
            2 => (Color.FromArgb("#0E7490"), Color.FromArgb("#0D9488")),
            3 => (Color.FromArgb("#4338CA"), Color.FromArgb("#7C3AED")),
            4 => (Color.FromArgb("#0369A1"), Color.FromArgb("#0EA5E9")),
            6 => (Color.FromArgb("#475569"), Color.FromArgb("#64748B")),
            _ => (Color.FromArgb("#B45309"), Color.FromArgb("#D97706"))
        };

        Grid head = new()
        {
            ColumnSpacing = 14,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)]
        };
        head.Add(new Border
        {
            WidthRequest = 56,
            HeightRequest = 56,
            StrokeThickness = 1,
            Stroke = new SolidColorBrush(Color.FromRgba(255, 255, 255, 70)),
            BackgroundColor = Color.FromRgba(255, 255, 255, 40),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 28 },
            Content = Icons.Glyph(Icons.PropaneTank, Colors.White, 30)
        }, 0, 0);
        head.Add(new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Spacing = 2,
            Children =
            {
                new Label { Text = card.StatusName, TextColor = Colors.White, FontSize = 22, FontFamily = "OpenSansSemibold" },
                new Label { Text = card.Location, TextColor = Color.FromRgba(255, 255, 255, 220), FontSize = 14, LineBreakMode = LineBreakMode.WordWrap }
            }
        }, 1, 0);

        HorizontalStackLayout badges = new() { Spacing = 8, Children = { Badge(card.Fullness == "-" ? "Dolu/boş bilinmiyor" : card.Fullness) } };
        if (card.DaysThere is int days)
        {
            badges.Children.Add(Badge(days == 0 ? "Bugün" : $"{days} gündür"));
        }

        if (!card.IsActive)
        {
            badges.Children.Add(Badge("Pasif kayıt"));
        }

        _result.Children.Add(new Border
        {
            Padding = new Thickness(18, 18),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 22 },
            Background = new LinearGradientBrush([new GradientStop(start, 0), new GradientStop(end, 1)], new Point(0, 0), new Point(1, 1)),
            Shadow = new Shadow { Brush = new SolidColorBrush(Colors.Black), Opacity = 0.22f, Radius = 14, Offset = new Point(0, 6) },
            Content = new VerticalStackLayout { Spacing = 14, Children = { head, badges } }
        });

        VerticalStackLayout rows = new() { Spacing = 12 };
        rows.Children.Add(Row(Icons.ManageSearch, "Barkod / seri no",
            card.Barcode == card.SerialNumber ? card.Barcode : $"{card.Barcode} · seri {card.SerialNumber}"));
        rows.Children.Add(Row(Icons.WaterDrop, "Gaz / kapasite", $"{card.GasType} · {card.Capacity}"));
        rows.Children.Add(Row(Icons.LocationOn, "Konum", card.Location));
        if (!string.IsNullOrWhiteSpace(card.Owner))
        {
            rows.Children.Add(Row(Icons.Handshake, "Tüpün sahibi", "Müşteri malı · " + card.Owner));
        }

        if (card.Trip is not null)
        {
            rows.Children.Add(Row(Icons.Route, "Sefer", card.Trip));
        }

        rows.Children.Add(Row(Icons.History, "Son hareket",
            card.LastMovementDate is null ? "Kayıt yok" : card.LastMovementDate + (card.LastMovementType is null ? string.Empty : "\n" + card.LastMovementType)));
        if (card.TestDue is not null)
        {
            string when = card.TestDays switch
            {
                < 0 and int late => $" · {-late} gün geçti",
                0 => " · bugün",
                int left => $" · {left} gün kaldı",
                _ => string.Empty
            };
            rows.Children.Add(Row(Icons.Event, "Test / son kullanma", card.TestDue + when, card.TestOverdue ? Color.FromArgb("#DC2626") : null));
        }

        _result.Children.Add(Ui.Card(rows));
    }

    private static Border Badge(string text) => new()
    {
        Padding = new Thickness(10, 4),
        StrokeThickness = 0,
        BackgroundColor = Color.FromRgba(255, 255, 255, 45),
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
        Content = new Label { Text = text, TextColor = Colors.White, FontSize = 13, FontFamily = "OpenSansSemibold" }
    };

    private static Grid Row(string glyph, string caption, string value, Color? valueColor = null)
    {
        Grid row = new()
        {
            ColumnSpacing = 12,
            ColumnDefinitions = [new ColumnDefinition(new GridLength(28)), new ColumnDefinition(GridLength.Star)]
        };
        row.Add(Icons.Glyph(glyph, Ui.Accent, 22), 0, 0);
        Label text = new() { Text = value, FontSize = 15, LineBreakMode = LineBreakMode.WordWrap };
        if (valueColor is not null)
        {
            text.TextColor = valueColor;
            text.FontFamily = "OpenSansSemibold";
        }

        row.Add(new VerticalStackLayout { Spacing = 1, Children = { Ui.Note(caption), text } }, 1, 0);
        return row;
    }
}
