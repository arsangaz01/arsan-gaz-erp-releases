using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Data.Common;
using ArsanGazERP.Data;
using ArsanGazERP.Services;
using ModelContextProtocol.Server;

namespace ArsanGazERP.ErpMcpServer;

[McpServerToolType]
public static class ErpTools
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static string DatabasePath => ArsanDatabase.Describe(DatabaseKind.Erp);

    [McpServerTool(Name = "erp_ozet", ReadOnly = true), Description("ERP'nin genel durumu: müşteri sayısı, durumlarına göre tüp sayıları, testi 30 gün içinde dolacak tüpler ve son 30 günün hareket sayısı. Salt okunur.")]
    public static string Summary()
    {
        using DbConnection connection = Open();
        StringBuilder text = new();
        text.AppendLine($"Aktif müşteri: {Scalar(connection, "SELECT COUNT(*) FROM Customers WHERE IsActive = TRUE")}");
        text.AppendLine("Tüpler (aktif):");
        using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = ArsanDatabase.Sql("""
                SELECT Status, COUNT(*), SUM(CASE WHEN IsFull = TRUE THEN 1 ELSE 0 END), SUM(CASE WHEN IsFull = FALSE THEN 1 ELSE 0 END)
                FROM Cylinders WHERE IsActive = TRUE GROUP BY Status ORDER BY Status
                """);
            using DbDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                long full = reader.GetInt64(2);
                long empty = reader.GetInt64(3);
                string split = full + empty > 0 ? $" (dolu {full} · boş {empty})" : string.Empty;
                text.AppendLine($"- {StatusName(reader.GetInt32(0))}: {reader.GetInt64(1)}{split}");
            }
        }

        text.AppendLine($"{CylinderTracking.LongAtCustomerDays} günden uzun süredir müşteride: {Scalar(connection, "SELECT COUNT(*) FROM Cylinders WHERE IsActive = TRUE AND Status = 3 AND LastMovementAt IS NOT NULL AND LastMovementAt < $limit", ("$limit", DateTime.Today.AddDays(-CylinderTracking.LongAtCustomerDays)))}");
        DateTime limit = DateTime.Today.AddDays(30);
        text.AppendLine($"Testi 30 gün içinde dolacak veya geçmiş tüp: {Scalar(connection, "SELECT COUNT(*) FROM Cylinders WHERE IsActive = TRUE AND TestDueDate IS NOT NULL AND TestDueDate <= $limit", ("$limit", limit))}");
        text.AppendLine($"Son 30 günde tüp hareketi: {Scalar(connection, "SELECT COUNT(*) FROM CylinderMovements WHERE MovementDate >= $since", ("$since", DateTime.Today.AddDays(-30)))}");
        return text.ToString();
    }

    [McpServerTool(Name = "musteri_ara", ReadOnly = true), Description("Müşteriyi ad, kod veya şehir parçasıyla arar; müşterideki tüp sayısını da verir. En fazla 25 sonuç. Salt okunur.")]
    public static string FindCustomers([Description("Aranacak metin (ad, kod veya şehir parçası)")] string arama)
    {
        string term = arama.Trim();
        if (term.Length == 0)
        {
            return "Arama metni boş olamaz.";
        }

        using DbConnection connection = Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = ArsanDatabase.Sql("""
            SELECT c.Id, c.Code, c.CompanyName, c.City, c.Phone, c.IsActive,
                   (SELECT COUNT(*) FROM Cylinders y WHERE y.CurrentCustomerId = c.Id AND y.Status = 3)
            FROM Customers c
            ORDER BY c.CompanyName
            """);
        List<string> rows = [];
        using DbDataReader reader = command.ExecuteReader();
        while (reader.Read() && rows.Count < 25)
        {
            string code = reader.GetString(1);
            string name = reader.GetString(2);
            string city = Text(reader, 3);
            if (!Contains(name, term) && !Contains(code, term) && !Contains(city, term))
            {
                continue;
            }

            rows.Add($"- #{reader.GetInt32(0)} {name} ({code}) · {city} · tel: {Text(reader, 4)} · müşterideki tüp: {reader.GetInt64(6)}{(reader.GetBoolean(5) ? "" : " · PASİF")}");
        }

        return rows.Count == 0 ? $"'{term}' ile eşleşen müşteri yok." : string.Join(Environment.NewLine, rows);
    }

    [McpServerTool(Name = "tup_ara", ReadOnly = true), Description("Tüpü barkod veya seri numarasıyla bulur; durumunu, bulunduğu müşteriyi ve son 10 hareketini verir. Salt okunur.")]
    public static string FindCylinder([Description("Tüp barkodu veya seri numarası")] string kod)
    {
        string code = kod.Trim();
        using DbConnection connection = Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = ArsanDatabase.Sql("""
            SELECT y.Id, y.SerialNumber, y.Barcode, y.GasType, y.Capacity, y.CapacityUnit, y.Status, y.TestDueDate, y.LastMovementAt, c.CompanyName, y.IsFull, y.VehiclePlate
            FROM Cylinders y LEFT JOIN Customers c ON c.Id = y.CurrentCustomerId
            WHERE lower(y.Barcode) = lower($code) OR lower(y.SerialNumber) = lower($code)
            LIMIT 1
            """);
        ArsanDatabase.AddParameter(command, "$code", code);
        StringBuilder text = new();
        int id;
        using (DbDataReader reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return $"'{code}' barkodlu veya seri numaralı tüp bulunamadı.";
            }

            id = reader.GetInt32(0);
            text.AppendLine($"Seri no: {reader.GetString(1)} · barkod: {reader.GetString(2)}");
            text.AppendLine($"Gaz: {reader.GetString(3)} · kapasite: {reader.GetDecimal(4).ToString("0.##", Turkish)} {reader.GetString(5)}");
            text.AppendLine($"Durum: {StatusName(reader.GetInt32(6))}{(reader.IsDBNull(11) ? "" : " · plaka: " + reader.GetString(11))}{(reader.IsDBNull(9) ? "" : " · müşteri: " + reader.GetString(9))}");
            text.AppendLine($"Dolu/boş: {CylinderTracking.FullnessName(reader.IsDBNull(10) ? null : reader.GetBoolean(10))}");
            text.AppendLine($"Test tarihi: {Date(reader, 7)} · son hareket: {Date(reader, 8)}");
        }

        using DbCommand movements = connection.CreateCommand();
        movements.CommandText = ArsanDatabase.Sql("""
            SELECT m.MovementDate, m.MovementType, m.FromStatus, m.ToStatus, c.CompanyName, m.Description, m.VehiclePlate
            FROM CylinderMovements m LEFT JOIN Customers c ON c.Id = m.CustomerId
            WHERE m.CylinderId = $id ORDER BY m.MovementDate DESC LIMIT 10
            """);
        ArsanDatabase.AddParameter(movements, "$id", id);
        using DbDataReader rows = movements.ExecuteReader();
        text.AppendLine("Son hareketler:");
        bool any = false;
        while (rows.Read())
        {
            any = true;
            text.AppendLine($"- {Date(rows, 0, "dd.MM.yyyy HH:mm")} · {rows.GetString(1)} · {StatusName(rows.GetInt32(2))} → {StatusName(rows.GetInt32(3))}{(rows.IsDBNull(6) ? "" : " · " + rows.GetString(6))}{(rows.IsDBNull(4) ? "" : " · " + rows.GetString(4))}{(rows.IsDBNull(5) ? "" : " · " + rows.GetString(5))}");
        }

        if (!any)
        {
            text.AppendLine("- Hareket kaydı yok.");
        }

        return text.ToString();
    }

    [McpServerTool(Name = "musterideki_tupler", ReadOnly = true), Description("Şu an müşterilerde duran tüpleri müşteri bazında listeler; en uzun süredir dönmeyenler önce. İsteğe bağlı müşteri adı filtresi. Salt okunur.")]
    public static string CylindersAtCustomers([Description("İsteğe bağlı müşteri adı parçası; boşsa tüm müşteriler")] string? musteri = null)
    {
        using DbConnection connection = Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = ArsanDatabase.Sql("""
            SELECT c.CompanyName, y.SerialNumber, y.GasType, y.LastMovementAt
            FROM Cylinders y JOIN Customers c ON c.Id = y.CurrentCustomerId
            WHERE y.Status = 3 AND y.IsActive = TRUE
            ORDER BY y.LastMovementAt
            """);
        List<string> rows = [];
        using DbDataReader reader = command.ExecuteReader();
        while (reader.Read() && rows.Count < 100)
        {
            string name = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(musteri) && !Contains(name, musteri.Trim()))
            {
                continue;
            }

            int? elapsed = reader.IsDBNull(3) ? null : (DateTime.Today - reader.GetDateTime(3).Date).Days;
            string days = elapsed is int value
                ? $"{value} gündür müşteride{(value > CylinderTracking.LongAtCustomerDays ? " ⚠" : "")}"
                : "teslim tarihi yok";
            rows.Add($"- {name} · {reader.GetString(1)} ({reader.GetString(2)}) · {days}");
        }

        return rows.Count == 0 ? "Müşteride duran tüp kaydı yok." : string.Join(Environment.NewLine, rows);
    }

    [McpServerTool(Name = "uzun_suredir_musteride", ReadOnly = true), Description("Verilen günden uzun süredir müşteride duran (dönmeyen) tüpleri en eskiden başlayarak listeler. Salt okunur.")]
    public static string LongAtCustomer([Description("Kaç günden uzun (7-365, varsayılan 60)")] int gun = CylinderTracking.LongAtCustomerDays)
    {
        int days = Math.Clamp(gun, 7, 365);
        using DbConnection connection = Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = ArsanDatabase.Sql("""
            SELECT c.CompanyName, y.SerialNumber, y.GasType, y.LastMovementAt
            FROM Cylinders y JOIN Customers c ON c.Id = y.CurrentCustomerId
            WHERE y.Status = 3 AND y.IsActive = TRUE AND y.LastMovementAt IS NOT NULL AND y.LastMovementAt < $limit
            ORDER BY y.LastMovementAt LIMIT 200
            """);
        ArsanDatabase.AddParameter(command, "$limit", DateTime.Today.AddDays(-days));
        List<string> rows = [];
        using DbDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add($"- {reader.GetString(0)} · {reader.GetString(1)} ({reader.GetString(2)}) · {(DateTime.Today - reader.GetDateTime(3).Date).Days} gündür müşteride");
        }

        return rows.Count == 0 ? $"{days} günden uzun süredir müşteride duran tüp yok." : $"{rows.Count} tüp {days} günden uzun süredir müşteride:{Environment.NewLine}{string.Join(Environment.NewLine, rows)}";
    }

    [McpServerTool(Name = "test_tarihi_yaklasan_tupler", ReadOnly = true), Description("Periyodik test tarihi verilen gün sayısı içinde dolacak veya geçmiş tüpleri listeler. Salt okunur.")]
    public static string TestDue([Description("Kaç gün ilerisine bakılacak (1-365, varsayılan 30)")] int gun = 30)
    {
        int days = Math.Clamp(gun, 1, 365);
        using DbConnection connection = Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = ArsanDatabase.Sql("""
            SELECT y.SerialNumber, y.GasType, y.TestDueDate, y.Status, c.CompanyName
            FROM Cylinders y LEFT JOIN Customers c ON c.Id = y.CurrentCustomerId
            WHERE y.IsActive = TRUE AND y.TestDueDate IS NOT NULL AND y.TestDueDate <= $limit
            ORDER BY y.TestDueDate LIMIT 200
            """);
        ArsanDatabase.AddParameter(command, "$limit", DateTime.Today.AddDays(days));
        List<string> rows = [];
        using DbDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            DateTime due = reader.GetDateTime(2).Date;
            int left = (due - DateTime.Today).Days;
            string timing = left < 0 ? $"{-left} gün geçti" : left == 0 ? "bugün" : $"{left} gün kaldı";
            rows.Add($"- {reader.GetString(0)} ({reader.GetString(1)}) · test: {due:dd.MM.yyyy} ({timing}) · {StatusName(reader.GetInt32(3))}{(reader.IsDBNull(4) ? "" : " · " + reader.GetString(4))}");
        }

        return rows.Count == 0 ? $"Önümüzdeki {days} gün içinde testi dolacak tüp yok (test tarihi girilmemiş tüpler dahil edilmez)." : string.Join(Environment.NewLine, rows);
    }

    [McpServerTool(Name = "siparis_tahmini", ReadOnly = true), Description("Müşterilerin geçmiş teslim aralıklarına göre bir sonraki alım tarihini tahmin eder; gecikmişler önce. En az 3 farklı günde teslim kaydı gerekir. Salt okunur.")]
    public static string ReorderForecasts()
    {
        using DbConnection connection = Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = ArsanDatabase.Sql("""
            SELECT m.CustomerId, c.CompanyName, m.MovementDate
            FROM CylinderMovements m JOIN Customers c ON c.Id = m.CustomerId
            WHERE m.ToStatus = 3
            """);
        List<CustomerDelivery> deliveries = [];
        using (DbDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                deliveries.Add(new CustomerDelivery(reader.GetInt32(0), reader.GetString(1), reader.GetDateTime(2)));
            }
        }

        List<ReorderForecast> forecasts = ReorderPrediction.Compute(deliveries, DateTime.Today);
        return forecasts.Count == 0
            ? $"Tahmin için en az {ReorderPrediction.MinimumDeliveryDays} farklı günde teslim kaydı olan müşteri yok."
            : string.Join(Environment.NewLine, forecasts.Take(50).Select(forecast =>
                $"- {forecast.CustomerName} · beklenen: {forecast.ExpectedNext:dd.MM.yyyy} ({forecast.Timing}) · genelde {forecast.TypicalIntervalDays} günde bir · son teslim {forecast.LastDelivery:dd.MM.yyyy}"));
    }

    [McpServerTool(Name = "son_hareketler", ReadOnly = true), Description("Son N gündeki tüp hareketlerini (teslim, iade, dolum vb.) listeler. En fazla 200 kayıt. Salt okunur.")]
    public static string RecentMovements([Description("Kaç gün geriye bakılacak (1-90, varsayılan 7)")] int gun = 7)
    {
        int days = Math.Clamp(gun, 1, 90);
        using DbConnection connection = Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = ArsanDatabase.Sql("""
            SELECT m.MovementDate, y.SerialNumber, m.MovementType, m.FromStatus, m.ToStatus, c.CompanyName
            FROM CylinderMovements m
            JOIN Cylinders y ON y.Id = m.CylinderId
            LEFT JOIN Customers c ON c.Id = m.CustomerId
            WHERE m.MovementDate >= $since
            ORDER BY m.MovementDate DESC LIMIT 200
            """);
        ArsanDatabase.AddParameter(command, "$since", DateTime.Today.AddDays(-days));
        List<string> rows = [];
        using DbDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add($"- {Date(reader, 0, "dd.MM.yyyy HH:mm")} · {reader.GetString(1)} · {reader.GetString(2)} · {StatusName(reader.GetInt32(3))} → {StatusName(reader.GetInt32(4))}{(reader.IsDBNull(5) ? "" : " · " + reader.GetString(5))}");
        }

        return rows.Count == 0 ? $"Son {days} günde tüp hareketi yok." : string.Join(Environment.NewLine, rows);
    }

    public static string CrmDatabasePath => ArsanDatabase.Describe(DatabaseKind.Crm);

    [McpServerTool(Name = "crm_ozet", ReadOnly = true), Description("CRM'deki firma sayıları (duruma göre), bugüne kadar takibi gelmiş firmalar ve son 7 günün aktivite sayısı. Salt okunur.")]
    public static string CrmSummary()
    {
        using DbConnection connection = Open(DatabaseKind.Crm);
        StringBuilder text = new();
        text.AppendLine($"Toplam firma: {Scalar(connection, "SELECT COUNT(*) FROM CrmCompanies")}");
        text.AppendLine("Duruma göre:");
        using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = ArsanDatabase.Sql("SELECT Status, COUNT(*) FROM CrmCompanies GROUP BY Status ORDER BY COUNT(*) DESC");
            using DbDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                text.AppendLine($"- {Text(reader, 0)}: {reader.GetInt64(1)}");
            }
        }

        text.AppendLine($"Takip zamanı gelmiş/geçmiş: {Scalar(connection, "SELECT COUNT(*) FROM CrmCompanies WHERE FollowUpAtUtc IS NOT NULL AND FollowUpAtUtc <= $now", ("$now", DateTime.UtcNow))}");
        text.AppendLine($"Son 7 günde aktivite: {Scalar(connection, "SELECT COUNT(*) FROM CrmActivities WHERE ActivityAtUtc >= $since", ("$since", DateTime.UtcNow.AddDays(-7)))}");
        return text.ToString();
    }

    [McpServerTool(Name = "crm_firma_ara", ReadOnly = true), Description("CRM firmasını ad, şehir veya sektör parçasıyla arar; durum, puan, takip tarihi ve son aktiviteyi verir. En fazla 25 sonuç. Salt okunur.")]
    public static string FindCrmCompanies([Description("Aranacak metin (firma adı, şehir veya sektör parçası)")] string arama)
    {
        string term = arama.Trim();
        if (term.Length == 0)
        {
            return "Arama metni boş olamaz.";
        }

        using DbConnection connection = Open(DatabaseKind.Crm);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = ArsanDatabase.Sql("""
            SELECT c.Id, c.CompanyName, c.City, c.Sector, c.Status, c.Score, c.Phone, c.FollowUpAtUtc,
                   (SELECT a.ActivityType || ': ' || a.Description FROM CrmActivities a WHERE a.CrmCompanyId = c.Id ORDER BY a.ActivityAtUtc DESC LIMIT 1)
            FROM CrmCompanies c
            ORDER BY c.Score DESC, c.CompanyName
            """);
        List<string> rows = [];
        using DbDataReader reader = command.ExecuteReader();
        while (reader.Read() && rows.Count < 25)
        {
            string name = reader.GetString(1);
            if (!Contains(name, term) && !Contains(Text(reader, 2), term) && !Contains(Text(reader, 3), term))
            {
                continue;
            }

            string followUp = reader.IsDBNull(7) ? "-" : reader.GetDateTime(7).ToLocalTime().ToString("dd.MM.yyyy", Turkish);
            string last = reader.IsDBNull(8) ? "-" : Shorten(reader.GetString(8), 120);
            rows.Add($"- #{reader.GetInt32(0)} {name} · {Text(reader, 2)} · {Text(reader, 3)} · durum: {Text(reader, 4)} · puan: {reader.GetInt32(5)} · tel: {Text(reader, 6)} · takip: {followUp} · son aktivite: {last}");
        }

        return rows.Count == 0 ? $"'{term}' ile eşleşen CRM firması yok." : string.Join(Environment.NewLine, rows);
    }

    [McpServerTool(Name = "crm_takip_listesi", ReadOnly = true), Description("Takip tarihi verilen gün sayısı içinde olan veya geçmiş CRM firmalarını listeler; en gecikmişler önce. Salt okunur.")]
    public static string CrmFollowUps([Description("Kaç gün ilerisine bakılacak (0-60, varsayılan 7)")] int gun = 7)
    {
        int days = Math.Clamp(gun, 0, 60);
        using DbConnection connection = Open(DatabaseKind.Crm);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = ArsanDatabase.Sql("""
            SELECT CompanyName, City, Status, Phone, FollowUpAtUtc
            FROM CrmCompanies
            WHERE FollowUpAtUtc IS NOT NULL AND FollowUpAtUtc <= $limit
            ORDER BY FollowUpAtUtc LIMIT 100
            """);
        ArsanDatabase.AddParameter(command, "$limit", DateTime.UtcNow.Date.AddDays(days + 1));
        List<string> rows = [];
        using DbDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            DateTime due = reader.GetDateTime(4).ToLocalTime().Date;
            int left = (due - DateTime.Today).Days;
            string timing = left < 0 ? $"{-left} gün gecikti" : left == 0 ? "bugün" : $"{left} gün kaldı";
            rows.Add($"- {reader.GetString(0)} · {Text(reader, 1)} · {Text(reader, 2)} · tel: {Text(reader, 3)} · takip: {due:dd.MM.yyyy} ({timing})");
        }

        return rows.Count == 0 ? $"Önümüzdeki {days} gün içinde takibi olan CRM firması yok." : string.Join(Environment.NewLine, rows);
    }

    private static string Shorten(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";

    private static DbConnection Open(DatabaseKind kind = DatabaseKind.Erp)
    {
        if (!ArsanDatabase.Exists(kind))
        {
            throw new InvalidOperationException("Veritabanı bulunamadı: " + ArsanDatabase.Describe(kind));
        }

        return ArsanDatabase.Open(kind, readOnly: true, new SqliteOptions(TimeoutSeconds: 15));
    }

    private static long Scalar(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = ArsanDatabase.Sql(sql);
        foreach ((string name, object value) in parameters)
        {
            ArsanDatabase.AddParameter(command, name, value);
        }

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool Contains(string value, string term) =>
        Turkish.CompareInfo.IndexOf(value, term, CompareOptions.IgnoreCase) >= 0;

    private static string Text(DbDataReader reader, int index) => reader.IsDBNull(index) ? "-" : reader.GetString(index);

    private static string Date(DbDataReader reader, int index, string format = "dd.MM.yyyy") =>
        reader.IsDBNull(index) ? "-" : reader.GetDateTime(index).ToString(format, Turkish);

    public static string StatusName(int status) => CylinderTracking.StatusName(status);
}
