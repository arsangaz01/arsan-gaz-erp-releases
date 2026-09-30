using ArsanGazERP.Modules.AI.Common;
using ArsanGazERP.Modules.AI.Models;
using ArsanGazERP.Modules.AI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
namespace ArsanGazERP.Modules.AI.ViewModels;
public partial class AgentDashboardViewModel(IAgentService agent, ICrmAnalyticsService crm, IMicrosoft365SummaryService m365) : ObservableObject
{
    public ObservableCollection<AgentActivityModel> RecentActivities { get; } = [];
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string agentStatus = "Hazır";
    [ObservableProperty] private string lastResult = string.Empty;
    [ObservableProperty] private DateTime? lastRunDate;
    [ObservableProperty] private int totalCustomers;
    [ObservableProperty] private int activeOffers;
    [ObservableProperty] private int openTasks;
    [ObservableProperty] private int todayMeetings;
    [ObservableProperty] private decimal monthlySales;
    [RelayCommand] private Task RunAgentAsync(CancellationToken ct) => RunAsync(AgentCommand.FullAnalysis, "AI Agent", ct);
    [RelayCommand] private Task AnalyzeCrmAsync(CancellationToken ct) => RunAsync(AgentCommand.CrmAnalysis, "CRM", ct);
    [RelayCommand] private Task GenerateM365SummaryAsync(CancellationToken ct) => RunAsync(AgentCommand.M365Summary, "Microsoft 365", ct);
    [RelayCommand] private Task CreateTaskPlanAsync(CancellationToken ct) => RunAsync(AgentCommand.TaskPlanning, "Görev Planı", ct);
    [RelayCommand] private Task ForecastSalesAsync(CancellationToken ct) => RunAsync(AgentCommand.SalesAnalysis, "Satış Tahmini", ct);
    [RelayCommand] private async Task LoadDashboardAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        try { IsBusy = true; var s = await crm.GetDashboardStatisticsAsync(ct); TotalCustomers=s.TotalCustomers; ActiveOffers=s.ActiveOffers; OpenTasks=s.OpenTasks; MonthlySales=s.MonthlySales; TodayMeetings=await m365.GetTodayMeetingCountAsync(ct); Add("Dashboard", "Dashboard güncellendi."); }
        catch(Exception ex) { AgentStatus="Hata"; Add("Hata", ex.Message); }
        finally { IsBusy=false; }
    }
    private async Task RunAsync(string command, string category, CancellationToken ct)
    {
        if (IsBusy) return;
        try { IsBusy=true; AgentStatus="Çalışıyor"; Add(category,"İşlem başlatıldı."); LastResult=await agent.ExecuteAsync(command,ct); LastRunDate=DateTime.Now; AgentStatus="Tamamlandı"; Add(category,LastResult); }
        catch(OperationCanceledException) { AgentStatus="İptal edildi"; Add(category,"İşlem iptal edildi."); }
        catch(Exception ex) { AgentStatus="Hata"; Add("Hata",ex.Message); }
        finally { IsBusy=false; }
    }
    private void Add(string category,string description) { RecentActivities.Insert(0,new(DateTime.Now,category,description)); while(RecentActivities.Count>100) RecentActivities.RemoveAt(100); }
}
