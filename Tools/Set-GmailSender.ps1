<#
.SYNOPSIS
  Kampanya e-postaları için Google Workspace / Gmail göndericisini ayarlar.
.DESCRIPTION
  Uygulama şifresi yalnızca bu Windows kullanıcısının açabileceği şekilde (DPAPI) şifrelenip
  %LOCALAPPDATA%\ArsanGazERP\Gmail\gmail-sender.bin dosyasına yazılır. Ardından adresin kendisine
  bir test e-postası gönderilir. Şifre ekrana, loglara veya Telegram'a yazılmaz.

  Uygulama şifresi: Google hesabı > Güvenlik > 2 Adımlı Doğrulama açık olmalı >
  https://myaccount.google.com/apppasswords adresinden "ArsanGazERP" adıyla 16 haneli şifre oluşturun.
  Workspace yöneticisi uygulama şifrelerini kapattıysa Admin konsolundan izin verilmelidir.
#>
param(
    [string]$Address = 'ozelgazlar@arsangaz.com',
    [string]$DisplayName = 'Arsan Gaz',
    [switch]$SkipTest
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$secure = Read-Host -AsSecureString "$Address için 16 haneli uygulama şifresi"
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $password = ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)) -replace '\s', ''
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}
if ($password.Length -lt 8) { throw 'Uygulama şifresi boş veya çok kısa.' }

if (-not $SkipTest) {
    $client = New-Object System.Net.Mail.SmtpClient('smtp.gmail.com', 587)
    $client.EnableSsl = $true
    $client.UseDefaultCredentials = $false
    $client.Credentials = New-Object System.Net.NetworkCredential($Address, $password)
    $message = New-Object System.Net.Mail.MailMessage
    $message.From = New-Object System.Net.Mail.MailAddress($Address, $DisplayName)
    $message.To.Add($Address)
    $message.Subject = 'ArsanGazERP kampanya göndericisi testi'
    $message.Body = 'Bu e-posta ArsanGazERP kampanya göndericisinin doğru ayarlandığını doğrulamak için gönderildi.'
    $message.SubjectEncoding = [Text.Encoding]::UTF8
    $message.BodyEncoding = [Text.Encoding]::UTF8
    try {
        $client.Send($message)
        Write-Host "Test e-postası $Address adresine gönderildi." -ForegroundColor Green
    } catch {
        $reason = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
        Write-Host "Gmail girişi başarısız: $reason" -ForegroundColor Red
        Write-Host 'Ayar kaydedilmedi. 2 Adımlı Doğrulama ve uygulama şifresini kontrol edin.' -ForegroundColor Yellow
        exit 1
    } finally {
        $message.Dispose()
        $client.Dispose()
    }
}

$json = [ordered]@{ Address = $Address; AppPassword = $password; DisplayName = $DisplayName } | ConvertTo-Json -Compress
$bytes = [Text.Encoding]::UTF8.GetBytes($json)
$protected = [Security.Cryptography.ProtectedData]::Protect($bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
[Array]::Clear($bytes, 0, $bytes.Length)
$directory = Join-Path $env:LOCALAPPDATA 'ArsanGazERP\Gmail'
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$path = Join-Path $directory 'gmail-sender.bin'
[IO.File]::WriteAllBytes("$path.tmp", $protected)
Move-Item -Force "$path.tmp" $path
Write-Host "Gmail göndericisi kaydedildi: $Address" -ForegroundColor Green
