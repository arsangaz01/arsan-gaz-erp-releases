using System.Diagnostics;
using ArsanGazERP.Data;
using ArsanGazERP.MobileApi;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;

// Runs the desktop data code against the PostgreSQL database configured in db-connection.json (or
// ARSANGAZ_PG_CONNECTION). Only this process switches provider. Writes create clearly marked test rows and remove
// them again; the consent round trip leaves ledger rows, so run it only against a test copy (--with-consent).
//   ArsanGazERP.PgHarness [--with-consent]
Environment.SetEnvironmentVariable("ARSANGAZ_DB_PROVIDER", "postgres");
Console.OutputEncoding = System.Text.Encoding.UTF8;
bool withConsent = args.Contains("--with-consent", StringComparer.Ordinal);
int failures = 0;
CrudService crud = new();
Console.WriteLine("Hedef: " + ArsanDatabase.Describe(DatabaseKind.Erp));

await Check("ERP/CRM şeması hazır", async () =>
{
    await using ArsanGazDbContext erp = new();
    await ArsanDbEf.EnsurePostgresSchemaAsync(erp, DatabaseKind.Erp);
    await using CrmDbContext crm = new();
    await ArsanDbEf.EnsurePostgresSchemaAsync(crm, DatabaseKind.Crm);
    return "tamam";
});
await Check("CrudService.CustomersAsync", async () => $"{(await crud.CustomersAsync()).Count} müşteri");
await Check("CrudService.CustomerChoicesAsync", async () => $"{(await crud.CustomerChoicesAsync()).Count} seçenek (ERP + CRM)");
await Check("CrudService.ProductsAsync", async () => $"{(await crud.ProductsAsync()).Count} ürün");
await Check("CrudService.CylindersAsync", async () => $"{(await crud.CylindersAsync()).Count} tüp");
await Check("CrudService.KnownGasTypesAsync", async () => $"{(await crud.KnownGasTypesAsync()).Count} gaz türü");
await Check("CrudService.CylinderOverviewAsync", async () => { await crud.CylinderOverviewAsync(); return "tamam"; });
await Check("CrudService.CylinderListAsync", async () => $"{(await crud.CylinderListAsync(null, null, false)).Count} satır");
await Check("CrudService.KnownVehiclePlatesAsync", async () => $"{(await crud.KnownVehiclePlatesAsync()).Count} plaka");
await Check("CrudService.ReorderForecastsAsync", async () => $"{(await crud.ReorderForecastsAsync()).Count} tahmin");
await Check("CrudService.OverdueInvoicesAsync", async () => $"{(await crud.OverdueInvoicesAsync()).Count} fatura");
await Check("CrudService.TasksAsync", async () => $"{(await crud.TasksAsync()).Count} görev");
await Check("CustomerChoices.SearchAsync(\"\")", async () => $"{(await CustomerChoices.SearchAsync(null)).Count} sonuç");
await Check("CustomerChoices.SearchAsync(\"gaz\")", async () => $"{(await CustomerChoices.SearchAsync("gaz")).Count} sonuç");

string marker = "PGTEST-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
await Check("Yazma: CRM firmasından müşteri + tüp kaydı, hareket, temizlik", async () =>
{
    await using CrmDbContext crm = new();
    int crmId = await crm.CrmCompanies.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
    int maxCustomerBefore = await MaxAsync(db => db.Customers.Select(x => (int?)x.Id));
    Cylinder created = await crud.RegisterCylinderAsync(marker, marker.ToLowerInvariant() + "-bc", "Oksijen", 10m, "Litre", true, DateTime.Today.AddYears(1), "PG harness", -crmId);
    int customerId = created.CurrentCustomerId ?? throw new InvalidOperationException("Müşteri bağlanmadı.");
    string? warning = await crud.MoveCylinderAsync(created.Id, CylinderStatus.Warehouse, null, "PG harness iade", "İade", null, false);
    Cylinder? found = await crud.FindCylinderByCodeAsync(marker.ToLowerInvariant());
    if (found?.Id != created.Id)
    {
        throw new InvalidOperationException("Seri numarası büyük/küçük harf duyarsız bulunamadı.");
    }

    await using ArsanGazDbContext db = new();
    int movements = await db.CylinderMovements.CountAsync(x => x.CylinderId == created.Id);
    DateTime stamp = await db.CylinderMovements.Where(x => x.CylinderId == created.Id).MaxAsync(x => x.MovementDate);
    if (Math.Abs((stamp - DateTime.Now).TotalMinutes) > 5)
    {
        throw new InvalidOperationException($"Hareket zamanı yerel saatle uyuşmuyor: {stamp:O}");
    }

    db.CylinderMovements.RemoveRange(db.CylinderMovements.Where(x => x.CylinderId == created.Id));
    db.Cylinders.Remove(await db.Cylinders.SingleAsync(x => x.Id == created.Id));
    await db.SaveChangesAsync();
    bool newCustomer = customerId > maxCustomerBefore;
    if (newCustomer)
    {
        await crud.DeleteCustomerAsync(customerId);
    }

    return $"tüp Id {created.Id}, müşteri Id {customerId} ({(newCustomer ? "yeni, silindi" : "mevcut")}), {movements} hareket, saat {stamp:HH:mm}; test kayıtları silindi" + (warning is null ? "" : "; uyarı: " + warning);
});

await Check("Yazma: ürün ekle/sil (identity sırası)", async () =>
{
    int maxBefore = await MaxAsync(db => db.Products.Select(x => (int?)x.Id));
    Product product = await crud.SaveProductAsync(new Product { Name = marker + " ürün", SalesPrice = 12.34m, VatRate = 20 });
    await crud.DeleteProductAsync(product.Id);
    if (product.Id <= maxBefore)
    {
        throw new InvalidOperationException($"Yeni Id {product.Id} mevcut en büyük Id {maxBefore} değerinden büyük değil.");
    }

    return $"yeni Id {product.Id} > önceki en büyük {maxBefore}; silindi";
});

string ownerSid = OperatingSystem.IsWindows()
    ? System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value
    : Environment.GetEnvironmentVariable("ARSANGAZ_OWNER_SID") ?? "S-1-5-HARNESS";
CampaignStore store = CampaignStore.CreateForPostgres(Path.Combine(Path.GetTempPath(), "arsangaz-harness-attachments"), ownerSid);
await Check("CampaignStore.InitializeAsync", async () => { await store.InitializeAsync(); return store.UsesPostgres ? "PostgreSQL" : "SQLite?!"; });
await Check("CampaignStore.GetExecutionEnabledAsync", async () => (await store.GetExecutionEnabledAsync()) ? "açık" : "kapalı");
await Check("CampaignStore.ListGroupsAsync", async () => $"{(await store.ListGroupsAsync(default)).Count} grup");
await Check("CampaignStore.ListAsync", async () => $"{(await store.ListAsync(new CampaignQuery(), default)).TotalCount} kampanya");
await Check("CampaignStore.GetConsentsAsync", async () => $"{(await store.GetConsentsAsync(null, null, default)).Count} izin kaydı");
await Check("CampaignStore.GetAudienceContactsAsync(CRM, \"gaz\")", async () => $"{(await store.GetAudienceContactsAsync(CampaignPopulation.CrmCompanies, "gaz", 500, default)).Count} kişi");
await Check("CampaignStore.PreviewAudienceAsync(CRM, e-posta, tümü)", async () =>
{
    AudiencePreview preview = await store.PreviewAudienceAsync(new AudienceSelection(CampaignPopulation.CrmCompanies, [], true), CampaignChannel.Email, default);
    return $"{preview.Recipients.Count} uygun, hariç: {string.Join(", ", preview.Exclusions.Select(x => $"{x.Reason}={x.Count}"))}";
});
await Check("CampaignStore grup kaydet/sil (benzersiz ad)", async () =>
{
    IReadOnlyList<CampaignContactOption> contacts = await store.GetAudienceContactsAsync(CampaignPopulation.CrmCompanies, null, 3, default);
    CampaignGroup group = await store.SaveGroupAsync(null, marker + " grup", CampaignPopulation.CrmCompanies, contacts.Select(x => x.ContactId).ToArray(), default);
    bool duplicateRejected = false;
    try
    {
        await store.SaveGroupAsync(null, (marker + " GRUP").ToLowerInvariant(), CampaignPopulation.CrmCompanies, [contacts[0].ContactId], default);
    }
    catch (InvalidOperationException)
    {
        duplicateRejected = true;
    }

    await store.DeleteGroupAsync(group.Id, default);
    if (!duplicateRejected)
    {
        throw new InvalidOperationException("Aynı adlı (büyük/küçük harf farklı) grup reddedilmedi.");
    }

    return $"{group.ContactIds.Count} üyeli grup kaydedildi, aynı ad reddedildi, silindi";
});

if (withConsent)
{
    await Check("CampaignStore WhatsApp izni ver/geri çek (CrmCompanies bayrak + zaman damgası)", async () =>
    {
        foreach (CampaignContactOption contact in await store.GetAudienceContactsAsync(CampaignPopulation.CrmCompanies, null, 2000, default))
        {
            if (!contact.IsActive || string.IsNullOrWhiteSpace(contact.Phone))
            {
                continue;
            }

            try
            {
                await store.SetConsentAsync(CampaignPopulation.CrmCompanies, contact.ContactId, CampaignChannel.WhatsApp, contact.Phone, true, "PG harness test", default, overrideCrmOptOut: true);
            }
            catch (ArgumentException)
            {
                continue;
            }

            AudiencePreview granted = await store.PreviewAudienceAsync(new AudienceSelection(CampaignPopulation.CrmCompanies, [contact.ContactId], false), CampaignChannel.WhatsApp, default);
            await store.SetConsentAsync(CampaignPopulation.CrmCompanies, contact.ContactId, CampaignChannel.WhatsApp, contact.Phone, false, "PG harness test geri çekme", default);
            AudiencePreview withdrawn = await store.PreviewAudienceAsync(new AudienceSelection(CampaignPopulation.CrmCompanies, [contact.ContactId], false), CampaignChannel.WhatsApp, default);
            await using CrmDbContext crm = new();
            CrmCompany row = await crm.CrmCompanies.AsNoTracking().SingleAsync(x => x.Id == contact.ContactId);
            if (granted.Recipients.Count != 1 || withdrawn.Recipients.Count != 0 || row.WhatsAppMarketingOptIn || row.WhatsAppOptOutAtUtc is null)
            {
                throw new InvalidOperationException($"izin sonrası uygun={granted.Recipients.Count}, geri çekme sonrası uygun={withdrawn.Recipients.Count}, bayrak={row.WhatsAppMarketingOptIn}");
            }

            return $"firma Id {contact.ContactId}: izinle 1 uygun, geri çekince 0; CRM bayrağı ve zaman damgası doğru";
        }

        throw new InvalidOperationException("Telefonu geçerli aktif CRM firması bulunamadı.");
    });
}

Console.WriteLine(failures == 0 ? "SONUÇ: TÜM KONTROLLER GEÇTİ" : $"SONUÇ: {failures} KONTROL BAŞARISIZ");
return failures == 0 ? 0 : 1;

async Task Check(string name, Func<Task<string>> action)
{
    Stopwatch watch = Stopwatch.StartNew();
    try
    {
        string result = await action();
        Console.WriteLine($"OK    {name}: {result} ({watch.ElapsedMilliseconds} ms)");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"HATA  {name}: {exception.GetType().Name}: {exception.Message}");
    }
}

static async Task<int> MaxAsync(Func<ArsanGazDbContext, IQueryable<int?>> query)
{
    await using ArsanGazDbContext db = new();
    return await query(db).MaxAsync() ?? 0;
}
