using System.Collections.ObjectModel;
using System.Globalization;
using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

public sealed class CrmPage : ContentPage
{
    private readonly bool _followUps;
    private readonly ObservableCollection<CrmItem> _items = [];
    private readonly SearchBar _search = new() { Placeholder = "Firma, şehir veya sektör ara" };
    private readonly Picker _range = new() { Title = "Aralık", ItemsSource = new[] { "Bugün ve gecikenler", "7 gün", "30 gün" }, SelectedIndex = 1 };
    private readonly Label _status = Ui.Note(string.Empty);
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };

    public CrmPage(bool followUps)
    {
        _followUps = followUps;
        Title = followUps ? "Takip" : "CRM";
        Ui.ApplyPageChrome(this);
        _search.SearchButtonPressed += async (_, _) => await LoadAsync();
        _range.SelectedIndexChanged += async (_, _) => await LoadAsync();

        CollectionView list = new()
        {
            ItemsSource = _items,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                Label name = new() { FontSize = 16, FontFamily = "OpenSansSemibold" };
                name.SetBinding(Label.TextProperty, nameof(CrmItem.Name));
                Label subtitle = Ui.Note(string.Empty);
                subtitle.SetBinding(Label.TextProperty, nameof(CrmItem.Subtitle));
                Label followUp = new() { FontSize = 13, TextColor = Ui.Accent, FontFamily = "OpenSansSemibold" };
                followUp.SetBinding(Label.TextProperty, nameof(CrmItem.FollowUpText));
                Border row = new()
                {
                    Margin = new Thickness(12, 0, 12, 10),
                    Padding = new Thickness(16, 12),
                    StrokeThickness = 1,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
                    Content = new VerticalStackLayout { Spacing = 4, Children = { name, subtitle, followUp } }
                };
                row.SetAppThemeColor(VisualElement.BackgroundColorProperty, Ui.SurfaceLight, Ui.SurfaceDark);
                row.SetAppTheme<Brush>(Border.StrokeProperty,
                    new SolidColorBrush(Color.FromArgb("#E7E5E4")), new SolidColorBrush(Color.FromArgb("#3F3F46")));
                return row;
            })
        };
        list.SelectionChanged += async (_, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is CrmItem item)
            {
                list.SelectedItem = null;
                await ShowActionsAsync(item);
            }
        };

        View header = followUps
            ? Ui.Card(new VerticalStackLayout { Spacing = 8, Children = { _range, _status } })
            : Ui.Card(new VerticalStackLayout { Spacing = 8, Children = { _search, _status } });
        header.Margin = new Thickness(12, 12, 12, 4);
        Grid root = new() { RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)] };
        root.Add(header, 0, 0);
        root.Add(_busy, 0, 1);
        root.Add(list, 0, 2);
        Content = root;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_followUps)
        {
            await LoadAsync();
        }
    }

    private Task LoadAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        List<CrmItem> items;
        if (_followUps)
        {
            int days = _range.SelectedIndex switch { 0 => 0, 2 => 30, _ => 7 };
            items = await ApiClient.Instance.FollowUpsAsync(days);
            _status.Text = items.Count == 0 ? "Bu aralıkta takip yok." : $"{items.Count} takip";
        }
        else
        {
            string query = (_search.Text ?? string.Empty).Trim();
            if (query.Length < 2)
            {
                _status.Text = "En az 2 harf yazın.";
                return;
            }

            items = await ApiClient.Instance.CrmAsync(query);
            _status.Text = items.Count == 0 ? "Eşleşen firma yok." : $"{items.Count} firma (en fazla 40 gösterilir)";
        }

        _items.Clear();
        foreach (CrmItem item in items)
        {
            _items.Add(item);
        }
    });

    private async Task ShowActionsAsync(CrmItem item)
    {
        string? choice = await DisplayActionSheetAsync(item.Name, "Kapat", null, "📞 Ara", "💬 WhatsApp aç", "📝 Not ekle", "📅 Takip tarihi ver");
        switch (choice)
        {
            case "📞 Ara":
                await Ui.OpenPhoneAsync(this, item.Phone);
                break;
            case "💬 WhatsApp aç":
                await Ui.OpenWhatsAppAsync(this, item.Phone);
                break;
            case "📝 Not ekle":
                string? note = await DisplayPromptAsync("Not ekle", item.Name, "Kaydet", "Vazgeç", "Görüşme notu", 1500);
                if (!string.IsNullOrWhiteSpace(note))
                {
                    await Ui.RunAsync(this, _busy, async () =>
                        await DisplayAlertAsync("CRM", await ApiClient.Instance.AddCrmNoteAsync(item.Id, note), "Tamam"));
                }

                break;
            case "📅 Takip tarihi ver":
                await ScheduleAsync(item);
                break;
        }
    }

    private async Task ScheduleAsync(CrmItem item)
    {
        string? choice = await DisplayActionSheetAsync("Takip tarihi", "Vazgeç", null, "Yarın", "3 gün sonra", "1 hafta sonra", "2 hafta sonra", "1 ay sonra", "Tarih yaz");
        DateTime? date = choice switch
        {
            "Yarın" => DateTime.Today.AddDays(1),
            "3 gün sonra" => DateTime.Today.AddDays(3),
            "1 hafta sonra" => DateTime.Today.AddDays(7),
            "2 hafta sonra" => DateTime.Today.AddDays(14),
            "1 ay sonra" => DateTime.Today.AddMonths(1),
            _ => null
        };
        if (choice == "Tarih yaz")
        {
            string? typed = await DisplayPromptAsync("Takip tarihi", "GG.AA.YYYY", "Tamam", "Vazgeç", DateTime.Today.AddDays(7).ToString("dd.MM.yyyy"));
            if (DateTime.TryParseExact(typed?.Trim(), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
            {
                date = parsed;
            }
            else if (typed is not null)
            {
                await DisplayAlertAsync("Takip", "Tarih GG.AA.YYYY biçiminde olmalı.", "Tamam");
            }
        }

        if (date is not DateTime value)
        {
            return;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            await DisplayAlertAsync("CRM", await ApiClient.Instance.SetFollowUpAsync(item.Id, value, null), "Tamam");
            if (_followUps)
            {
                await LoadAsync();
            }
        });
    }
}
