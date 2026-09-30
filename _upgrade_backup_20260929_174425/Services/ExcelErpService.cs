using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace ArsanGazERP.Services;

public sealed class ExcelErpService
{
    public string? WorkbookPath { get; private set; }

    public void SelectWorkbook(string path)
    {
        if (!File.Exists(path) || !path.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("Geçerli XLSM dosyası bulunamadı.", path);
        }

        using SpreadsheetDocument document = SpreadsheetDocument.Open(path, false);
        _ = document.WorkbookPart ?? throw new InvalidOperationException("Çalışma kitabı okunamadı.");
        WorkbookPath = path;
    }

    public IReadOnlyList<string> GetSheetNames()
    {
        EnsureSelected();
        using SpreadsheetDocument document = SpreadsheetDocument.Open(WorkbookPath!, false);
        Sheets sheets = document.WorkbookPart!.Workbook.Sheets
            ?? throw new InvalidOperationException("Sayfa listesi bulunamadı.");

        return sheets.Elements<Sheet>()
            .Select(sheet => sheet.Name?.Value ?? "Adsız")
            .ToList();
    }

    public string CreateBackup()
    {
        EnsureSelected();
        string workbookDirectory = Path.GetDirectoryName(WorkbookPath!)
            ?? throw new InvalidOperationException("Dosya klasörü bulunamadı.");
        string backupDirectory = Path.Combine(workbookDirectory, "Yedekler");
        Directory.CreateDirectory(backupDirectory);

        string target = Path.Combine(
            backupDirectory,
            $"ARSAN_AI_ERP_{DateTime.Now:yyyyMMdd_HHmmss}.xlsm");

        File.Copy(WorkbookPath!, target, false);
        return target;
    }

    private void EnsureSelected()
    {
        if (string.IsNullOrWhiteSpace(WorkbookPath))
        {
            throw new InvalidOperationException("Önce ARSAN_AI_ERP_v1.0.xlsm dosyasını seçin.");
        }
    }
}
