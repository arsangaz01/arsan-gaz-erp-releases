using ArsanGazERP.Data;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ArsanGazERP.Views;

public sealed class AiDashboardWindow : Window
{
    private const string Project = @"C:\ArsanGazERP\ArsanGazERP_V2_Duzeltilmis";
    private static readonly string Queue = Path.Combine(Project, "Tools", "AgentWatcherData", "Queue");
    private readonly TextBlock customerValue = ValueText();
    private readonly TextBlock invoiceValue = ValueText();
    private readonly TextBlock overdueValue = ValueText();
    private readonly TextBlock agentRunValue = ValueText();
    private readonly TextBlock m365Value = BodyText();
    private readonly TextBlock excelValue = BodyText();
    private readonly TextBlock nvidiaValue = BodyText();

    private readonly TextBox output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10), MinHeight = 160 };

    public AiDashboardWindow()
    {
        Title = "Arsan Gaz ERP V7.6.1 AI Dashboard";
        Width = 1160; Height = 760; MinWidth = 980; MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));
        Content = Build();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private UIElement Build()
    {
        Grid root = new() { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        DockPanel header = new() { Margin = new Thickness(0, 0, 0, 14) };
        Button refresh = ActionButton("Yenile", async () => await RefreshAsync());
        DockPanel.SetDock(refresh, Dock.Right); header.Children.Add(refresh);
        StackPanel titles = new();
        titles.Children.Add(new TextBlock { Text = "AI Dashboard", FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Brushes.Black });
        titles.Children.Add(new TextBlock { Text = "CRM, Microsoft 365 ve AI görevlerini tek merkezden yönetin", FontSize = 13, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0) });
        header.Children.Add(titles); Grid.SetRow(header, 0); root.Children.Add(header);

        UniformGrid stats = new() { Columns = 4, Margin = new Thickness(0, 0, 0, 14) };
        stats.Children.Add(Card("Müşteriler", customerValue)); stats.Children.Add(Card("Faturalar", invoiceValue)); stats.Children.Add(Card("Geciken", overdueValue)); stats.Children.Add(Card("Agent Çalışmaları", agentRunValue));
        Grid.SetRow(stats, 1); root.Children.Add(stats);

        Grid stateGrid = new() { Margin = new Thickness(0, 0, 0, 14) };
        stateGrid.ColumnDefinitions.Add(new ColumnDefinition()); stateGrid.ColumnDefinitions.Add(new ColumnDefinition()); stateGrid.ColumnDefinitions.Add(new ColumnDefinition()); stateGrid.ColumnDefinitions.Add(new ColumnDefinition());

        Grid.SetRow(stateGrid, 2); root.Children.Add(stateGrid);

        Grid lower = new(); lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) }); lower.ColumnDefinitions.Add(new ColumnDefinition());
        StackPanel actions = new() { Margin = new Thickness(0, 0, 14, 0) };
        actions.Children.Add(Section("AI Görevleri"));
        actions.Children.Add(ActionButton("Tam AI Analizi", () => QueueAsync("ERP Analizi", "ERP, CRM, Microsoft 365, Excel ve satış verileri için birleşik yönetici analizi hazırla.")));
        actions.Children.Add(ActionButton("CRM Analizi", () => QueueAsync("CRM Analizi", "Riskli müşterileri, bekleyen teklifleri, takipleri ve satış fırsatlarını analiz et.")));
        actions.Children.Add(ActionButton("Microsoft 365 Özeti", () => QueueAsync("Microsoft 365 Özeti", "Günlük e-posta, takvim ve görev özetini yönetici formatında hazırla.")));
        actions.Children.Add(ActionButton("Satış Tahmini", () => QueueAsync("Satış Tahmini", "Mevcut CRM ve satış kayıtlarına göre satış tahmini ve takip planı hazırla.")));
        actions.Children.Add(ActionButton("Görev Planı", () => QueueAsync("Görev Planlama", "Bugün için önceliklendirilmiş CRM, tahsilat, teklif ve Microsoft 365 görev planı hazırla.")));
        Button control = ActionButton("Agent Control Center'ı Aç", OpenControlCenter); control.Margin = new Thickness(0, 16, 0, 0); actions.Children.Add(control);
        lower.Children.Add(actions);

        Border logCard = new() { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(215, 220, 228)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12) };
        Grid logGrid = new(); logGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); logGrid.RowDefinitions.Add(new RowDefinition());
        logGrid.Children.Add(Section("Sonuç ve Etkinlik")); Grid.SetRow(output, 1); logGrid.Children.Add(output); logCard.Child = logGrid; Grid.SetColumn(logCard, 1); lower.Children.Add(logCard);
        Grid.SetRow(lower, 3); root.Children.Add(lower); return root;
    }

    private async Task RefreshAsync()
    {
        try
        {
            await using ArsanGazDbContext db = new();
            int customers = await db.Customers.CountAsync();
            int invoices = await db.Invoices.CountAsync();
            int overdue = await db.Invoices.CountAsync(x => x.DueDate < DateTime.Today && x.PaidAmount < x.TotalAmount);
            int runs = await db.AgentRuns.CountAsync();
            customerValue.Text = customers.ToString("N0", CultureInfo.GetCultureInfo("tr-TR"));
            invoiceValue.Text = invoices.ToString("N0", CultureInfo.GetCultureInfo("tr-TR"));
            overdueValue.Text = overdue.ToString("N0", CultureInfo.GetCultureInfo("tr-TR"));
            agentRunValue.Text = runs.ToString("N0", CultureInfo.GetCultureInfo("tr-TR"));

            var s = UnifiedConnectionStateService.Load();
            m365Value.Text = string.IsNullOrWhiteSpace(s.Account) ? "Bağlı değil" : "Bağlı: " + s.Account;
            excelValue.Text = !string.IsNullOrWhiteSpace(s.ExcelPath) && File.Exists(s.ExcelPath) ? "Bağlı: " + Path.GetFileName(s.ExcelPath) : "Seçilmedi";
            NvidiaNimService nim = new(); nvidiaValue.Text = nim.IsConfigured ? "Yapılandırıldı: " + nim.Model : "Bağlı değil";

            output.Text = $"Dashboard güncellendi: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
        }
        catch (Exception ex) { output.Text = "Dashboard yenileme hatası: " + ex.GetBaseException().Message; }
    }

    private async Task QueueAsync(string type, string title)
    {
        try
        {
            Directory.CreateDirectory(Queue);
            string id = Guid.NewGuid().ToString("N");
            var task = new { Id = id, Title = title, Type = type, Priority = "Yüksek", Provider = "NVIDIA NIM", Status = "Bekliyor", Created = DateTime.Now, Started = (DateTime?)null, Completed = (DateTime?)null, Report = (string?)null };
            string file = Path.Combine(Queue, id + ".task.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(task, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            output.Text = "Görev kuyruğa eklendi:" + Environment.NewLine + title + Environment.NewLine + Environment.NewLine + file;
        }
        catch (Exception ex) { output.Text = "Görev ekleme hatası: " + ex.GetBaseException().Message; }
    }

    private void OpenControlCenter()
    {
        string exe = Path.Combine(Project, "Tools", "AgentWatcher", "publish", "ArsanGazERP.AgentWatcher.exe");
        if (!File.Exists(exe)) { output.Text = "Agent Control Center bulunamadı: " + exe; return; }
        if (Process.GetProcessesByName("ArsanGazERP.AgentWatcher").Length == 0) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
        else output.Text = "Agent Control Center zaten çalışıyor.";
    }

    private static Border Card(string title, UIElement value)
    {
        StackPanel p = new();
        p.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 8) });
        p.Children.Add(value);
        return new Border { Child = p, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(215, 220, 228)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14), Margin = new Thickness(5) };
    }

    private static TextBlock ValueText() => new() { FontSize = 28, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(24, 94, 166)) };
    private static TextBlock BodyText() => new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkGreen };
    private static TextBlock Section(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(4, 4, 4, 10) };
    private static Button ActionButton(string text, Action action) { Button b = BaseButton(text); b.Click += (_, _) => action(); return b; }
    private static Button ActionButton(string text, Func<Task> action) { Button b = BaseButton(text); b.Click += async (_, _) => { b.IsEnabled = false; try { await action(); } finally { b.IsEnabled = true; } }; return b; }
    private static Button BaseButton(string text) => new() { Content = text, Height = 40, Margin = new Thickness(4), Padding = new Thickness(12, 4, 12, 4), HorizontalContentAlignment = HorizontalAlignment.Left };
    private static void AddAt(Grid grid, UIElement element, int column) { Grid.SetColumn(element, column); grid.Children.Add(element); }
}
