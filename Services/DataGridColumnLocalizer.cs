using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using ArsanGazERP.Models;

namespace ArsanGazERP.Services;

public static class DataGridColumnLocalizer
{
    private static readonly Dictionary<string, string> Headers = new(StringComparer.Ordinal)
    {
        ["Id"] = "No",
        ["Code"] = "Kod",
        ["Name"] = "Ad",
        ["CompanyName"] = "Firma",
        ["ContactName"] = "Yetkili",
        ["Phone"] = "Telefon",
        ["Email"] = "E-posta",
        ["City"] = "İl",
        ["District"] = "İlçe",
        ["Address"] = "Adres",
        ["TaxOffice"] = "Vergi dairesi",
        ["TaxNumber"] = "Vergi no",
        ["IsActive"] = "Aktif",
        ["CreatedAt"] = "Oluşturulma",
        ["UpdatedAt"] = "Güncelleme",
        ["Unit"] = "Birim",
        ["SalesPrice"] = "Satış fiyatı",
        ["Currency"] = "Para birimi",
        ["VatRate"] = "KDV %",
        ["CriticalStock"] = "Kritik stok",
        ["SerialNumber"] = "Seri no",
        ["Barcode"] = "Barkod",
        ["GasType"] = "Gaz türü",
        ["Capacity"] = "Kapasite",
        ["CapacityUnit"] = "Kapasite birimi",
        ["Status"] = "Durum",
        ["CurrentCustomerId"] = "Müşteri no",
        ["LastMovementAt"] = "Son hareket",
        ["TestDueDate"] = "Test tarihi",
        ["QuoteNumber"] = "Teklif no",
        ["CustomerId"] = "Müşteri no",
        ["QuoteDate"] = "Teklif tarihi",
        ["ValidUntil"] = "Geçerlilik",
        ["TotalAmount"] = "Toplam",
        ["FollowUpDate"] = "Takip tarihi",
        ["InvoiceNumber"] = "Fatura no",
        ["InvoiceDate"] = "Fatura tarihi",
        ["DueDate"] = "Vade",
        ["PaidAmount"] = "Ödenen",
        ["Title"] = "Başlık",
        ["Description"] = "Açıklama",
        ["Priority"] = "Öncelik",
        ["Category"] = "Kategori"
    };

    public static void Attach(params DataGrid[] grids)
    {
        foreach (DataGrid grid in grids)
            grid.AutoGeneratingColumn += OnAutoGeneratingColumn;
    }

    private static void OnAutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        Type type = Nullable.GetUnderlyingType(e.PropertyType) ?? e.PropertyType;
        bool navigation = type != typeof(string) &&
            (typeof(IEnumerable).IsAssignableFrom(type) || (type.IsClass && type.Namespace == typeof(Customer).Namespace));
        if (navigation)
        {
            e.Cancel = true;
            return;
        }

        if (Headers.TryGetValue(e.PropertyName, out string? header))
            e.Column.Header = header;

        if (e.Column is DataGridTextColumn text && text.Binding is Binding binding)
        {
            if (type == typeof(DateTime))
            {
                binding.StringFormat = "dd.MM.yyyy HH:mm";
                binding.ConverterCulture = CultureInfo.GetCultureInfo("tr-TR");
                e.Column.MinWidth = 120;
            }
            else if (type == typeof(decimal))
            {
                binding.StringFormat = "N2";
                binding.ConverterCulture = CultureInfo.GetCultureInfo("tr-TR");
            }
            else if (type == typeof(CylinderStatus))
            {
                binding.Converter = new CylinderStatusConverter();
            }
        }

        if (e.PropertyName == "Id")
            e.Column.Width = new DataGridLength(50);
    }

    private sealed class CylinderStatusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
        {
            CylinderStatus.Warehouse => "Depoda",
            CylinderStatus.Vehicle => "Araçta",
            CylinderStatus.Customer => "Müşteride",
            CylinderStatus.Filling => "Dolumda",
            CylinderStatus.Service => "Bakımda",
            CylinderStatus.Scrap => "Hurda",
            _ => value?.ToString() ?? string.Empty
        };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
