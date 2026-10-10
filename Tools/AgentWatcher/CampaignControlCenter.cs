using ArsanGazERP.Models;
using System.Windows.Forms;

namespace ArsanGazERP.AgentWatcher;

internal sealed partial class ControlCenterForm
{
    partial void BuildCampaigns()
    {
        tabs.Multiline = true;
        var page = new TabPage("Müşteri Kampanyaları");
        var list = new ListBox { Dock = DockStyle.Fill, FormattingEnabled = true };
        list.Format += (_, e) =>
        {
            if (e.ListItem is CampaignSnapshot campaign)
                e.Value = $"{campaign.Input.Name} | {(campaign.Input.Channel == CampaignChannel.Email ? "E-posta" : "WhatsApp")} | {StateLabel(campaign.State)}";
        };
        var detail = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill };
        var status = new Label { AutoSize = true, Text = "Gönderim varsayılan olarak kapalı. API'nin kabul etmesi teslimat anlamına gelmez." };
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(list);
        split.Panel2.Controls.Add(detail);
        bool runningAction = false;
        async Task RefreshCampaigns()
        {
            var selected = list.SelectedItem as CampaignSnapshot;
            bool enabled = await campaignService.RefreshExecutionStateAsync(closing.Token);
            var items = new List<CampaignSnapshot>();
            CampaignPage pageResult;
            do
            {
                pageResult = await campaignService.ListAsync(new(items.Count, 100), closing.Token);
                items.AddRange(pageResult.Items);
            } while (items.Count < pageResult.TotalCount && pageResult.Items.Count > 0);
            list.DataSource = items;
            status.Text = $"Genel gönderim {(enabled ? "AÇIK" : "KAPALI")} | {pageResult.TotalCount} kampanya | Belirsiz (Unknown) gönderimler tekrarlanmaz";
            if (selected is not null)
            {
                var snapshot = await campaignService.GetAsync(selected.Id, closing.Token);
                if (snapshot is not null)
                {
                    var outcomes = new List<CampaignRecipient>();
                    RecipientPage recipientPage;
                    do
                    {
                        recipientPage = await campaignService.GetRecipientsAsync(snapshot.Id, outcomes.Count, 500, closing.Token);
                        outcomes.AddRange(recipientPage.Items);
                    } while (outcomes.Count < recipientPage.TotalCount && recipientPage.Items.Count > 0);
                    detail.Text = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Campaign = snapshot,
                        NextEligibleUtc = await campaignService.GetNextEligibleTimeAsync(snapshot.Id, closing.Token),
                        Recipients = outcomes
                    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    list.SelectedItem = list.Items.Cast<CampaignSnapshot>().FirstOrDefault(item => item.Id == snapshot.Id);
                }
            }
        }
        void ActionButton(string label, Func<Task> action)
        {
            var button = new Button { Text = label, AutoSize = true };
            button.Click += async (_, _) =>
            {
                if (runningAction) return;
                runningAction = true;
                try { await action(); }
                catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    status.Text = "Kampanya işlemi başarısız: " + exception.Message;
                    AppLog.Write("CAMPAIGN-UI", exception.ToString());
                    MessageBox.Show(this, exception.Message, "Kampanya işlemi", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { runningAction = false; }
            };
            actions.Controls.Add(button);
        }
        CampaignSnapshot Selected() => list.SelectedItem as CampaignSnapshot ?? throw new InvalidOperationException("Kampanya seçin.");
        bool Confirm(string text) => MessageBox.Show(this, text, "Gerçek gönderim onayı", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        ActionButton("Yenile / seçili ayrıntısı", RefreshCampaigns);
        ActionButton("Tüm gönderimleri durdur", async () => { await campaignService.DisableExecutionAsync(closing.Token); await RefreshCampaigns(); });
        ActionButton("Duraklat", async () => { var item = Selected(); await campaignService.PauseAsync(item.Id, item.Revision, closing.Token); await RefreshCampaigns(); });
        ActionButton("Devam et", async () =>
        {
            var item = Selected();
            if (!Confirm("Onaylı kampanyanın gerçek müşteri gönderimlerine, onaylı maliyet ve göndericiyle devam edilsin mi?")) return;
            await campaignService.ResumeAsync(item.Id, item.Revision, new(true, true, true), closing.Token);
            await RefreshCampaigns();
        });
        ActionButton("İptal et", async () =>
        {
            var item = Selected();
            if (!Confirm("Kampanya iptal edilsin mi? Devam eden veya kabul edilmiş mesaj geri alınamaz.")) return;
            await campaignService.CancelAsync(item.Id, item.Revision, closing.Token);
            await RefreshCampaigns();
        });
        ActionButton("ERP düzenleyicisi hakkında", () =>
        {
            detail.Text = "Taslak, ekler, izin kanıtı, onay ve etkinleştirme ERP ana ekranındaki Müşteri Kampanyaları düğmesindedir. Gruplar ve hızlı gönderim sekmesinden gruba toplu gönderim hazırlanır. Bu panel belirsiz gönderimleri tekrar etmez.";
            return Task.CompletedTask;
        });
        var header = new Panel { Dock = DockStyle.Top, Height = 90 };
        status.Dock = DockStyle.Bottom;
        header.Controls.Add(actions);
        header.Controls.Add(status);
        page.Controls.Add(split);
        page.Controls.Add(header);
        tabs.TabPages.Add(page);
    }

    static string StateLabel(CampaignState state) => state switch
    {
        CampaignState.Draft => "Taslak",
        CampaignState.Approved => "Onaylandı",
        CampaignState.Scheduled => "Zamanlandı",
        CampaignState.Active => "Etkin",
        CampaignState.Paused => "Duraklatıldı",
        CampaignState.CancelRequested => "İptal isteniyor",
        CampaignState.Cancelled => "İptal edildi",
        CampaignState.Completed => "Tamamlandı",
        CampaignState.Faulted => "Hatalı",
        _ => state.ToString()
    };
}
