using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed class ExcelSyncService
{
    public async Task<string> ImportProductsAsync(string workbookPath)
    {
        using SpreadsheetDocument document = SpreadsheetDocument.Open(workbookPath, false);
        WorkbookPart workbook = document.WorkbookPart ?? throw new InvalidOperationException("Çalışma kitabı okunamadı.");
        Sheet? sheet = workbook.Workbook.Sheets!.Elements<Sheet>()
            .FirstOrDefault(x => string.Equals(x.Name?.Value, "Urunler", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(x.Name?.Value, "Ürünler", StringComparison.OrdinalIgnoreCase));
        if (sheet?.Id?.Value is null) throw new InvalidOperationException("Urunler/Ürünler sayfası bulunamadı.");
        WorksheetPart worksheet = (WorksheetPart)workbook.GetPartById(sheet.Id.Value);
        SharedStringTable? shared = workbook.SharedStringTablePart?.SharedStringTable;
        List<List<string>> rows = worksheet.Worksheet.Descendants<Row>()
            .Select(r => r.Elements<Cell>().Select(c => Read(c, shared)).ToList()).ToList();
        if (rows.Count < 2) return "Ürün satırı bulunamadı.";
        List<string> headers = rows[0].Select(Normalize).ToList();
        int nameIx = Find(headers, "urun", "ürün");
        int priceIx = Find(headers, "birim fiyat", "fiyat");
        int currencyIx = Find(headers, "para birimi", "doviz", "döviz");
        int vatIx = Find(headers, "kdv");
        if (nameIx < 0) throw new InvalidOperationException("Ürün adı sütunu bulunamadı.");
        await using ArsanGazDbContext db = new();
        int added=0, updated=0;
        foreach (List<string> row in rows.Skip(1))
        {
            string name = Get(row,nameIx).Trim(); if(string.IsNullOrWhiteSpace(name)) continue;
            string code = MakeCode(name);
            Product? product = await db.Products.FirstOrDefaultAsync(x=>x.Code==code);
            bool isNew = product is null;
            product ??= new Product { Code=code, Name=name };
            product.Name=name;
            if(TryDecimal(Get(row,priceIx),out decimal price)) product.SalesPrice=price;
            string currency=Get(row,currencyIx).Trim().ToUpperInvariant(); if(currency.Length==3) product.Currency=currency;
            if(TryDecimal(Get(row,vatIx),out decimal vat)) product.VatRate=vat<=1?vat*100:vat;
            if(isNew){db.Products.Add(product);added++;}else updated++;
        }
        await db.SaveChangesAsync();
        return $"Excel ürün senkronizasyonu tamamlandı. Yeni: {added}, Güncellenen: {updated}.";
    }
    private static int Find(List<string> h,params string[] names)=>h.FindIndex(x=>names.Any(n=>x.Contains(Normalize(n))));
    private static string Get(List<string> r,int i)=>i>=0&&i<r.Count?r[i]:string.Empty;
    private static string Normalize(string s)=>s.Trim().ToLowerInvariant().Replace("ı","i").Replace("ş","s").Replace("ğ","g").Replace("ü","u").Replace("ö","o").Replace("ç","c");
    private static string MakeCode(string s)=>new string(Normalize(s).ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray()).PadRight(3,'X')[..Math.Min(Math.Max(3,new string(Normalize(s).Where(char.IsLetterOrDigit).ToArray()).Length),30)];
    private static bool TryDecimal(string s,out decimal value){s=s.Replace("TL","").Replace(" ","").Trim(); return decimal.TryParse(s,NumberStyles.Any,CultureInfo.GetCultureInfo("tr-TR"),out value)||decimal.TryParse(s,NumberStyles.Any,CultureInfo.InvariantCulture,out value);}
    private static string Read(Cell c,SharedStringTable? s){string v=c.CellValue?.InnerText??c.InnerText??""; if(c.DataType?.Value==CellValues.SharedString&&int.TryParse(v,out int i)&&s!=null)return s.ElementAt(i).InnerText; return v;}
}