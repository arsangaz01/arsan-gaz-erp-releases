using System;
using System.Collections.Generic;
using System.Linq;
using ArsanGazERP.Models;

namespace ArsanGazERP.Services;

public sealed class AgentService
{
    private readonly ExcelErpService _excelService;

    public AgentService(ExcelErpService excelService)
    {
        _excelService = excelService;
    }

    public IReadOnlyList<AgentFinding> Analyze()
    {
        List<AgentFinding> findings = new();
        IReadOnlyList<string> sheets = _excelService.GetSheetNames();
        string[] expectedSheets = { "AI Yönetim Paneli", "Teklif Formu", "Urunler", "Kur Bilgisi" };

        foreach (string expected in expectedSheets)
        {
            if (!sheets.Contains(expected, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new AgentFinding("Kritik", "Excel", $"Gerekli sayfa bulunamadı: {expected}"));
            }
        }

        if (findings.Count == 0)
        {
            findings.Add(new AgentFinding("Başarılı", "Excel", "Temel ERP sayfaları doğrulandı."));
        }

        findings.Add(new AgentFinding(
            "Bilgi",
            "Güvenlik",
            "Ajan analiz ve taslak üretir. Para transferi, resmî fatura, yüksek iskonto ve ilk müşteri iletişimi insan onayı gerektirir."));

        return findings;
    }
}
