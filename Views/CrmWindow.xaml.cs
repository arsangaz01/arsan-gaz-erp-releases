using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Views;

public partial class CrmWindow : Window
{
    private List<CrmCompany> _companies = new();
    private CrmCompany? _selected;
    public CrmWindow(){InitializeComponent();Loaded += async (_,_) => await LoadAsync();}

    private async Task LoadAsync()
    {
        await new CrmSchemaService().EnsureAsync();
        await using ArsanGazDbContext db = new();
        _companies = await db.CrmCompanies.AsNoTracking().OrderByDescending(x=>x.Score).ThenBy(x=>x.CompanyName).ToListAsync();
        ApplyFilter(); StatusText.Text = $"CRM firma sayısı: {_companies.Count:N0} · Son yenileme {DateTime.Now:HH:mm}";
    }
    private void ApplyFilter(){string q=SearchBox.Text.Trim();string status=(StatusFilter.SelectedItem as ComboBoxItem)?.Content?.ToString()??"Tümü";CompaniesGrid.ItemsSource=_companies.Where(x=>(string.IsNullOrWhiteSpace(q)||(x.CompanyName.Contains(q,StringComparison.CurrentCultureIgnoreCase)||(x.City?.Contains(q,StringComparison.CurrentCultureIgnoreCase)??false)||(x.Sector?.Contains(q,StringComparison.CurrentCultureIgnoreCase)??false)))&&(status=="Tümü"||x.Status==status)).ToList();}
    private async Task LoadCardAsync(int id){await using ArsanGazDbContext db=new();_selected=await db.CrmCompanies.AsNoTracking().FirstAsync(x=>x.Id==id);CompanyTitle.Text=_selected.CompanyName;PhoneBox.Text=_selected.Phone??"";EmailBox.Text=_selected.Email??"";WebsiteBox.Text=_selected.Website??"";AddressBox.Text=_selected.Address??"";foreach(ComboBoxItem item in CompanyStatus.Items)if(item.Content?.ToString()==_selected.Status){CompanyStatus.SelectedItem=item;break;}NotesGrid.ItemsSource=await db.CrmNotes.AsNoTracking().Where(x=>x.CrmCompanyId==id).OrderByDescending(x=>x.CreatedAtUtc).Select(x=>new NoteRow(x.CreatedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),x.NoteText)).ToListAsync();ActivitiesGrid.ItemsSource=await db.CrmActivities.AsNoTracking().Where(x=>x.CrmCompanyId==id).OrderByDescending(x=>x.ActivityAtUtc).Select(x=>new ActivityRow(x.ActivityAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),x.ActivityType,x.Description)).ToListAsync();}
    private async void ImportProspects_Click(object s,RoutedEventArgs e){try{await new CrmSchemaService().EnsureAsync();await using ArsanGazDbContext db=new();var existing=await db.CrmCompanies.Where(x=>x.SourceProspectId!=null).Select(x=>x.SourceProspectId!.Value).ToListAsync();var prospects=await db.Prospects.AsNoTracking().Where(x=>!existing.Contains(x.Id)).ToListAsync();foreach(var p in prospects)db.CrmCompanies.Add(new CrmCompany{SourceProspectId=p.Id,CompanyName=p.CompanyName,City=p.City,Sector=p.SectorQuery,Phone=p.Phone,Website=p.Website,Address=p.Address,Score=p.OpportunityScore,Status="Yeni"});await db.SaveChangesAsync();StatusText.Text=$"{prospects.Count:N0} aday CRM'e aktarıldı.";await LoadAsync();}catch(Exception ex){StatusText.Text=ex.GetBaseException().Message;}}
    private async void CompaniesGrid_SelectionChanged(object s,SelectionChangedEventArgs e){if(CompaniesGrid.SelectedItem is CrmCompany c)await LoadCardAsync(c.Id);}
    private void SearchBox_TextChanged(object s,TextChangedEventArgs e)=>ApplyFilter();
    private void StatusFilter_SelectionChanged(object s,SelectionChangedEventArgs e){if(IsLoaded)ApplyFilter();}
    private async void Refresh_Click(object s,RoutedEventArgs e)=>await LoadAsync();
    private async void SaveCompany_Click(object s,RoutedEventArgs e){if(_selected is null)return;await using ArsanGazDbContext db=new();var c=await db.CrmCompanies.FindAsync(_selected.Id);if(c is null)return;c.Phone=PhoneBox.Text.Trim();c.Email=EmailBox.Text.Trim();c.Website=WebsiteBox.Text.Trim();c.Address=AddressBox.Text.Trim();c.Status=(CompanyStatus.SelectedItem as ComboBoxItem)?.Content?.ToString()??c.Status;await db.SaveChangesAsync();StatusText.Text="Firma kartı kaydedildi.";await LoadAsync();await LoadCardAsync(c.Id);}
    private async void AddNote_Click(object s,RoutedEventArgs e){if(_selected is null||string.IsNullOrWhiteSpace(NoteBox.Text))return;await using ArsanGazDbContext db=new();db.CrmNotes.Add(new CrmNote{CrmCompanyId=_selected.Id,NoteText=NoteBox.Text.Trim()});db.CrmActivities.Add(new CrmActivity{CrmCompanyId=_selected.Id,ActivityType="Not",Description="CRM notu eklendi"});await db.SaveChangesAsync();NoteBox.Clear();await LoadCardAsync(_selected.Id);}
    private void Phone_Click(object s,RoutedEventArgs e){if(!string.IsNullOrWhiteSpace(PhoneBox.Text))Process.Start(new ProcessStartInfo("tel:"+PhoneBox.Text.Trim()){UseShellExecute=true});}
    private void WhatsApp_Click(object s,RoutedEventArgs e){string digits=new(PhoneBox.Text.Where(char.IsDigit).ToArray());if(digits.StartsWith("0"))digits="90"+digits[1..];if(!string.IsNullOrWhiteSpace(digits))Process.Start(new ProcessStartInfo("https://wa.me/"+digits){UseShellExecute=true});}
    private void Website_Click(object s,RoutedEventArgs e){string url=WebsiteBox.Text.Trim();if(!string.IsNullOrWhiteSpace(url))Process.Start(new ProcessStartInfo(url.StartsWith("http",StringComparison.OrdinalIgnoreCase)?url:"https://"+url){UseShellExecute=true});}
    private sealed record NoteRow(string CreatedAtLocal,string NoteText);
    private sealed record ActivityRow(string ActivityAtLocal,string ActivityType,string Description);
}
