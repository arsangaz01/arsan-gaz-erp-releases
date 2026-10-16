#Requires -Version 5.1
<#
.SYNOPSIS
  Yerel TSPL yazdırma ajanı — tarayıcıdaki Tüp Takip paneli TA310'a RAW TSPL gönderir.

.DESCRIPTION
  TA310 barkodu Windows sürücüsü üzerinden resim olarak değil, TSPL QRCODE komutuyla
  kendi içinde üretir. Panel http://127.0.0.1:39201 adresine TSPL yollar; bu betik
  yazıcı kuyruğuna RAW olarak basar.

  Kullanım: PowerShell'de bir kez (veya başlangıçta) çalıştırın:
    powershell -NoProfile -ExecutionPolicy Bypass -File TsplPrintAgent.ps1

  Durdurmak için Ctrl+C.
#>
param(
  [int]$Port = 39201,
  [string]$DefaultPrinter = "TSC TA310"
)

$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class TsplRawPrinter {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi)]
  public class DOCINFOA {
    [MarshalAs(UnmanagedType.LPStr)] public string pDocName;
    [MarshalAs(UnmanagedType.LPStr)] public string pOutputFile;
    [MarshalAs(UnmanagedType.LPStr)] public string pDataType;
  }
  [DllImport("winspool.drv", EntryPoint="OpenPrinterA", SetLastError=true, CharSet=CharSet.Ansi)]
  public static extern bool OpenPrinter(string name, out IntPtr h, IntPtr d);
  [DllImport("winspool.drv", SetLastError=true)] public static extern bool ClosePrinter(IntPtr h);
  [DllImport("winspool.drv", EntryPoint="StartDocPrinterA", SetLastError=true, CharSet=CharSet.Ansi)]
  public static extern bool StartDocPrinter(IntPtr h, int level, [In] DOCINFOA di);
  [DllImport("winspool.drv", SetLastError=true)] public static extern bool EndDocPrinter(IntPtr h);
  [DllImport("winspool.drv", SetLastError=true)] public static extern bool StartPagePrinter(IntPtr h);
  [DllImport("winspool.drv", SetLastError=true)] public static extern bool EndPagePrinter(IntPtr h);
  [DllImport("winspool.drv", SetLastError=true)] public static extern bool WritePrinter(IntPtr h, IntPtr p, int n, out int written);

  public static string Send(string printer, byte[] bytes) {
    IntPtr h;
    if (!OpenPrinter(printer, out h, IntPtr.Zero)) return "Yazıcı açılamadı: " + printer;
    try {
      var di = new DOCINFOA { pDocName = "ArsanTupTakip TSPL", pDataType = "RAW" };
      if (!StartDocPrinter(h, 1, di)) return "StartDocPrinter başarısız";
      try {
        if (!StartPagePrinter(h)) return "StartPagePrinter başarısız";
        try {
          IntPtr p = Marshal.AllocHGlobal(bytes.Length);
          try {
            Marshal.Copy(bytes, 0, p, bytes.Length);
            int w;
            if (!WritePrinter(h, p, bytes.Length, out w) || w != bytes.Length) return "WritePrinter başarısız";
          } finally { Marshal.FreeHGlobal(p); }
        } finally { EndPagePrinter(h); }
      } finally { EndDocPrinter(h); }
      return null;
    } finally { ClosePrinter(h); }
  }
}
"@

function Get-PrinterNames {
  try {
    return @(Get-Printer | Select-Object -ExpandProperty Name)
  } catch {
    return @($DefaultPrinter)
  }
}

function Send-Json($res, $code, $obj) {
  $json = ($obj | ConvertTo-Json -Compress -Depth 6)
  $bytes = [Text.Encoding]::UTF8.GetBytes($json)
  $res.StatusCode = $code
  $res.ContentType = "application/json; charset=utf-8"
  $res.AddHeader("Access-Control-Allow-Origin", "*")
  $res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
  $res.AddHeader("Access-Control-Allow-Headers", "Content-Type")
  $res.ContentLength64 = $bytes.Length
  $res.OutputStream.Write($bytes, 0, $bytes.Length)
  $res.OutputStream.Close()
}

$prefix = "http://127.0.0.1:$Port/"
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add($prefix)
try {
  $listener.Start()
} catch {
  Write-Host "Port $Port açılamadı. Başka bir TsplPrintAgent çalışıyor olabilir."
  throw
}

Write-Host "TsplPrintAgent dinliyor: $prefix"
Write-Host "Varsayılan yazıcı: $DefaultPrinter"
Write-Host "Durdurmak için Ctrl+C"

while ($listener.IsListening) {
  $ctx = $listener.GetContext()
  $req = $ctx.Request
  $res = $ctx.Response
  try {
    if ($req.HttpMethod -eq "OPTIONS") {
      $res.StatusCode = 204
      $res.AddHeader("Access-Control-Allow-Origin", "*")
      $res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
      $res.AddHeader("Access-Control-Allow-Headers", "Content-Type")
      $res.Close()
      continue
    }

    $path = $req.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant()
    if ($req.HttpMethod -eq "GET" -and ($path -eq "" -or $path -eq "/health")) {
      Send-Json $res 200 @{ ok = $true; printer = $DefaultPrinter; printers = @(Get-PrinterNames) }
      continue
    }

    if ($req.HttpMethod -eq "GET" -and $path -eq "/printers") {
      Send-Json $res 200 @{ printers = @(Get-PrinterNames) }
      continue
    }

    if ($req.HttpMethod -eq "POST" -and $path -eq "/print") {
      $reader = New-Object IO.StreamReader($req.InputStream, $req.ContentEncoding)
      $body = $reader.ReadToEnd()
      $reader.Close()
      $data = $body | ConvertFrom-Json
      $printer = if ($data.printer) { [string]$data.printer } else { $DefaultPrinter }
      $tspl = [string]$data.tspl
      if ([string]::IsNullOrWhiteSpace($tspl)) {
        Send-Json $res 400 @{ ok = $false; error = "tspl boş" }
        continue
      }
      $bytes = [Text.Encoding]::UTF8.GetBytes($tspl)
      $err = [TsplRawPrinter]::Send($printer, $bytes)
      if ($err) {
        Write-Host "$(Get-Date -Format HH:mm:ss) HATA $printer — $err"
        Send-Json $res 500 @{ ok = $false; error = $err }
      } else {
        Write-Host "$(Get-Date -Format HH:mm:ss) OK $printer — $($bytes.Length) bayt TSPL"
        Send-Json $res 200 @{ ok = $true; printer = $printer; bytes = $bytes.Length }
      }
      continue
    }

    Send-Json $res 404 @{ ok = $false; error = "bilinmeyen yol" }
  } catch {
    try { Send-Json $res 500 @{ ok = $false; error = $_.Exception.Message } } catch { $res.Abort() }
  }
}
