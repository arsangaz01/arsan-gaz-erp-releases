<#
.SYNOPSIS
  İzciPro (SQL Server) veritabanının tüm dolu tablolarını PostgreSQL COPY biçiminde dışa aktarır.

.DESCRIPTION
  Yedek önce SQL Server/LocalDB'ye geri yüklenmiş olmalıdır (salt okunur yeterli). Çıktı klasörü:
    schema.sql        izcipro şemasındaki tabloları oluşturur ve data\*.tsv.gz dosyalarını yükler (psql ile)
    data\<tablo>.tsv.gz
    manifest.txt      tablo / satır sayıları
  Şifre ve token sütunları ile kullanıcı/oturum tabloları hiçbir zaman dışa aktarılmaz.
#>
param(
    [string]$Server = "(localdb)\Gecis",
    [string]$Database = "IZCIPRO_GUNES_SB",
    [Parameter(Mandatory)][string]$OutDir
)

$ErrorActionPreference = "Stop"
$skipTables = @("san_TokenKeyRezerv", "tbl_ActiveDirectory_User", "tbl_SessionFollowUp", "tbl_KullaniciKonum")
$secretColumn = "(?i)sifre|şifre|password|parola|token|pwd"

Add-Type -TypeDefinition @"
using System;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

public static class IzciProExporter
{
    public static long Export(string connectionString, string sql, string path)
    {
        long rows = 0;
        using (SqlConnection cn = new SqlConnection(connectionString))
        {
            cn.Open();
            using (SqlCommand cmd = new SqlCommand(sql, cn))
            {
                cmd.CommandTimeout = 3600;
                using (SqlDataReader reader = cmd.ExecuteReader())
                using (FileStream file = File.Create(path))
                using (GZipStream gzip = new GZipStream(file, CompressionLevel.Optimal))
                using (StreamWriter writer = new StreamWriter(gzip, new UTF8Encoding(false), 1 << 16))
                {
                    writer.NewLine = "\n";
                    StringBuilder line = new StringBuilder();
                    while (reader.Read())
                    {
                        line.Clear();
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            if (i > 0) line.Append('\t');
                            line.Append(reader.IsDBNull(i) ? "\\N" : Format(reader.GetValue(i)));
                        }
                        writer.WriteLine(line.ToString());
                        rows++;
                    }
                }
            }
        }
        return rows;
    }

    private static string Format(object value)
    {
        if (value is bool) return (bool)value ? "t" : "f";
        if (value is DateTime) return ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        if (value is decimal) return ((decimal)value).ToString(CultureInfo.InvariantCulture);
        if (value is double) return ((double)value).ToString("R", CultureInfo.InvariantCulture);
        if (value is float) return ((float)value).ToString("R", CultureInfo.InvariantCulture);
        if (value is Guid) return ((Guid)value).ToString();
        if (value is byte[]) return "\\\\x" + BitConverter.ToString((byte[])value).Replace("-", "");
        string text = Convert.ToString(value, CultureInfo.InvariantCulture);
        StringBuilder escaped = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': escaped.Append("\\\\"); break;
                case '\t': escaped.Append("\\t"); break;
                case '\n': escaped.Append("\\n"); break;
                case '\r': escaped.Append("\\r"); break;
                case '\0': break;
                default: escaped.Append(c); break;
            }
        }
        return escaped.ToString();
    }
}
"@ -ReferencedAssemblies System.Data, System.IO.Compression

function Get-PgType([string]$sqlType) {
    switch ($sqlType) {
        "int" { "integer" } "smallint" { "smallint" } "tinyint" { "smallint" } "bigint" { "bigint" } "bit" { "boolean" }
        { $_ -in "datetime", "datetime2", "smalldatetime", "date" } { "timestamp without time zone" }
        { $_ -in "numeric", "decimal", "money", "smallmoney" } { "numeric" }
        "float" { "double precision" } "real" { "real" } "uniqueidentifier" { "uuid" }
        { $_ -in "varbinary", "binary", "image", "timestamp", "rowversion" } { "bytea" }
        default { "text" }
    }
}

$connection = "Server=$Server;Database=$Database;Integrated Security=true;Connect Timeout=60"
New-Item -ItemType Directory -Force -Path (Join-Path $OutDir "data") | Out-Null

$cn = New-Object System.Data.SqlClient.SqlConnection $connection
$cn.Open()
try {
    $cmd = $cn.CreateCommand()
    $cmd.CommandText = @"
SELECT t.name tbl, c.name col, ty.name typ, c.column_id
FROM sys.tables t JOIN sys.columns c ON c.object_id = t.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE t.is_ms_shipped = 0 AND EXISTS (SELECT 1 FROM sys.partitions p WHERE p.object_id = t.object_id AND p.index_id IN (0,1) AND p.rows > 0)
ORDER BY t.name, c.column_id
"@
    $da = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    $columns = New-Object System.Data.DataTable
    $null = $da.Fill($columns)
} finally { $cn.Close() }

$schema = New-Object System.Text.StringBuilder
$null = $schema.AppendLine("-- İzciPro ham arşivi: $Database ($(Get-Date -Format 'yyyy-MM-dd HH:mm'))")
$null = $schema.AppendLine("\set ON_ERROR_STOP on")
$null = $schema.AppendLine("BEGIN;")
$null = $schema.AppendLine("DROP SCHEMA IF EXISTS izcipro CASCADE;")
$null = $schema.AppendLine("CREATE SCHEMA izcipro;")
$manifest = New-Object System.Collections.Generic.List[string]

foreach ($group in ($columns.Rows | Group-Object { $_.tbl })) {
    $table = $group.Name
    if ($skipTables -contains $table) { $manifest.Add("$table`tATLANDI (kullanıcı/oturum tablosu)"); continue }
    $kept = @($group.Group | Where-Object { $_.col -notmatch $secretColumn -and (Get-PgType $_.typ) -ne "bytea" })
    $dropped = @($group.Group | Where-Object { $_.col -match $secretColumn }) | ForEach-Object { $_.col }
    if ($kept.Count -eq 0) { continue }

    $pgTable = $table.ToLowerInvariant()
    $defs = $kept | ForEach-Object { '"{0}" {1}' -f $_.col.ToLowerInvariant(), (Get-PgType $_.typ) }
    $null = $schema.AppendLine("CREATE TABLE izcipro.$pgTable ($($defs -join ', '));")
    $null = $schema.AppendLine("\copy izcipro.$pgTable FROM PROGRAM 'gzip -dc data/$pgTable.tsv.gz'")

    $select = "SELECT " + (($kept | ForEach-Object { "[{0}]" -f $_.col }) -join ", ") + " FROM dbo.[$table]"
    $started = Get-Date
    $rows = [IzciProExporter]::Export($connection, $select, (Join-Path $OutDir "data\$pgTable.tsv.gz"))
    $note = if ($dropped.Count -gt 0) { " (gizli sütun çıkarıldı: $($dropped -join ', '))" } else { "" }
    $manifest.Add("$pgTable`t$rows$note")
    Write-Host ("{0,-40} {1,10} satır  {2:n1} sn{3}" -f $pgTable, $rows, ((Get-Date) - $started).TotalSeconds, $note)
}

$null = $schema.AppendLine("COMMIT;")
$null = $schema.AppendLine("ANALYZE;")
[IO.File]::WriteAllText((Join-Path $OutDir "schema.sql"), $schema.ToString().Replace("`r`n", "`n"), (New-Object System.Text.UTF8Encoding $false))
[IO.File]::WriteAllLines((Join-Path $OutDir "manifest.txt"), $manifest, (New-Object System.Text.UTF8Encoding $false))
Write-Host "Tamam: $OutDir"
