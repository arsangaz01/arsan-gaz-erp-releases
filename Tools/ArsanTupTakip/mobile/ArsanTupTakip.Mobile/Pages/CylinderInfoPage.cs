using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// Read-only status of one cylinder: scan with the camera, read the barcode from a photo or type the code. No movements here.
public sealed class CylinderInfoPage : ContentPage
{
    private readonly Entry _code = Ui.TextField("Barkod veya seri no");
    private readonly VerticalStackLayout _result = new() { Spacing = 10 };
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };

    public CylinderInfoPage()
    {
        Title = "Tüp Bilgisi";
        Ui.ApplyPageChrome(this);
        _code.ReturnType = ReturnType.Search;
        _code.Completed += async (_, _) => await LookupAsync(_code.Text);

        Grid sources = new()
        {
            ColumnSpacing = 8,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)]
        };
        sources.Add(SourceButton(Icons.QrScanner, "Kamera ile\nokut", ScanAsync), 0, 0);
        sources.Add(SourceButton(Icons.Camera, "Fotoğraf\nçek", () => FromPhotoAsync(capture: true)), 1, 0);
        sources.Add(SourceButton(Icons.Gallery, "Galeriden\nseç", () => FromPhotoAsync(capture: false)), 2, 0);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Section(
                        "Tüp okut",
                        Ui.Note("Yalnızca tüpün durumu gösterilir; bu ekrandan hareket kaydedilmez."),
                        sources,
                        _code,
                        Ui.Primary("Sorgula", async (_, _) => await LookupAsync(_code.Text))),
                    _busy,
                    _result
                }
            }
        };
    }

    private static Border SourceButton(string glyph, string text, Func<Task> action)
    {
        Border tile = new()
        {
            Padding = new Thickness(6, 12),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            Background = new LinearGradientBrush(
                [new GradientStop(Ui.Navy, 0), new GradientStop(Ui.AccentDark, 1)], new Point(0, 0), new Point(1, 1)),
            Content = new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    Ui.IconLabel(glyph, Color.FromArgb("#5EEAD4"), 28),
                    new Label
                    {
                        Text = text,
                        FontSize = 12,
                        FontFamily = "OpenSansSemibold",
                        TextColor = Colors.White,
                        HorizontalTextAlignment = TextAlignment.Center
                    }
                }
            }
        };
        TapGestureRecognizer tap = new();
        tap.Tapped += async (_, _) =>
        {
            await tile.ScaleToAsync(0.94, 70);
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
                _code.Text = value;
                await LookupAsync(value);
            }
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Kamera", exception.Message, "Tamam");
        }
    }

    private async Task FromPhotoAsync(bool capture)
    {
        try
        {
            FileResult? file;
            if (capture)
            {
                file = await MediaPicker.Default.CapturePhotoAsync(new MediaPickerOptions { Title = "Barkodun fotoğrafı" });
            }
            else
            {
                List<FileResult>? picked = await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { Title = "Barkodlu görüntü", SelectionLimit = 1 });
                file = picked?.OfType<FileResult>().FirstOrDefault();
            }

            if (file is null)
            {
                return;
            }

            IReadOnlyList<string>? codes = null;
            await Ui.RunAsync(this, _busy, async () =>
            {
                await using Stream stream = await file.OpenReadAsync();
                codes = await ImageBarcodeReader.ReadAsync(stream);
            });
            if (codes is null)
            {
                return;
            }

            string? code = codes.Count switch
            {
                0 => null,
                1 => codes[0],
                _ => await DisplayActionSheetAsync($"{codes.Count} kod bulundu", "Vazgeç", null, [.. codes])
            };
            if (codes.Count == 0)
            {
                await DisplayAlertAsync("Barkod bulunamadı",
                    "Görüntüde okunabilir barkod veya QR kod yok. Barkodun net, düz ve tamamen göründüğü bir fotoğraf deneyin.", "Tamam");
                return;
            }

            if (code is not null && codes.Contains(code))
            {
                _code.Text = code;
                await LookupAsync(code);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Görüntü", exception.Message, "Tamam");
        }
    }

    private async Task LookupAsync(string? code)
    {
        string value = (code ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            await DisplayAlertAsync("Tüp Bilgisi", "Barkodu okutun veya seri numarasını yazın.", "Tamam");
            return;
        }

        _result.Children.Clear();
        await Ui.RunAsync(this, _busy, async () =>
        {
            try
            {
                Render(await ApiClient.Instance.CylinderStatusInfoAsync(value));
            }
            catch (ApiException notFound) when (notFound.StatusCode == 404)
            {
                _result.Children.Add(Ui.Card(Row(Icons.Error, "Kayıtlı değil", notFound.Message, Color.FromArgb("#DC2626"))));
            }
        });
    }

    private void Render(CylinderStatusInfo info)
    {
        Color statusColor = StatusColor(info.Status);
        Grid head = new()
        {
            ColumnSpacing = 14,
            ColumnDefinitions = [new ColumnDefinition(new GridLength(56)), new ColumnDefinition(GridLength.Star)]
        };
        Border badge = new()
        {
            WidthRequest = 56,
            HeightRequest = 56,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            BackgroundColor = statusColor.WithAlpha(0.15f),
            Content = Ui.IconLabel(Icons.Tank, statusColor, 32)
        };
        head.Add(badge, 0, 0);
        head.Add(new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Spacing = 2,
            Children =
            {
                new Label { Text = info.SerialNumber, FontSize = 20, FontFamily = "OpenSansSemibold" },
                Ui.Note(info.ManufacturerSerial is { Length: > 0 } maker ? "Üretici seri no: " + maker
                    : info.Barcode == info.SerialNumber ? "Barkod aynı" : "Barkod: " + info.Barcode),
                new Label
                {
                    Text = info.StatusName + (info.IsActive ? string.Empty : " · pasif"),
                    FontSize = 15,
                    FontFamily = "OpenSansSemibold",
                    TextColor = statusColor
                }
            }
        }, 1, 0);

        Color fullnessColor = info.IsFull switch
        {
            true => Color.FromArgb("#16A34A"),
            false => Color.FromArgb("#D97706"),
            null => Ui.Muted
        };
        VerticalStackLayout rows = new()
        {
            Spacing = 12,
            Children =
            {
                Row(Icons.Science, "Gaz / kapasite", $"{info.GasType} · {info.Capacity}", Ui.Accent),
                Row(info.IsFull == true ? Icons.Check : Icons.Inventory, "Dolu / boş", info.Fullness, fullnessColor),
                Row(info.Status == 1 ? Icons.Warehouse : info.Status == 2 ? Icons.Shipping : Icons.Place, "Şu an nerede",
                    info.Location + (info.DaysThere is int days && info.Status == 3 ? $" ({days} gündür)" : string.Empty), statusColor),
                Row(Icons.History, "Son hareket",
                    info.LastMovementDate is null ? "Hareket kaydı yok" : info.LastMovementDate + (info.LastMovementType is null ? string.Empty : "\n" + info.LastMovementType), Ui.Accent)
            }
        };
        if (info.TestDue is not null)
        {
            rows.Children.Add(Row(Icons.Event, "Test / son kullanma tarihi", info.TestDue + (info.TestOverdue ? " · süresi geçmiş!" : string.Empty),
                info.TestOverdue ? Color.FromArgb("#DC2626") : Ui.Accent));
        }

        if (info.NeedsReview)
        {
            rows.Children.Add(Row(Icons.Event, "Uyarı", "Eski sistemden eksik bilgiyle aktarıldı; yönetici incelemesi bekliyor.", Color.FromArgb("#D97706")));
        }

        _result.Children.Add(Ui.Card(head));
        _result.Children.Add(Ui.Card(rows));
        _result.Children.Add(Ui.Note($"Sorgulandı: {DateTime.Now:dd.MM.yyyy HH:mm}"));
    }

    private static Grid Row(string glyph, string caption, string value, Color color)
    {
        Grid row = new()
        {
            ColumnSpacing = 12,
            ColumnDefinitions = [new ColumnDefinition(new GridLength(36)), new ColumnDefinition(GridLength.Star)]
        };
        row.Add(Ui.IconLabel(glyph, color, 24), 0, 0);
        row.Add(new VerticalStackLayout
        {
            Spacing = 1,
            Children =
            {
                new Label { Text = caption, FontSize = 12, TextColor = Ui.Muted },
                new Label { Text = value, FontSize = 15, FontFamily = "OpenSansSemibold", LineBreakMode = LineBreakMode.WordWrap }
            }
        }, 1, 0);
        return row;
    }

    private static Color StatusColor(int status) => Color.FromArgb(status switch
    {
        1 => "#0D9488",
        2 => "#4F46E5",
        3 => "#2563EB",
        4 => "#0891B2",
        5 => "#D97706",
        6 => "#6B7280",
        _ => "#6B7280"
    });
}
