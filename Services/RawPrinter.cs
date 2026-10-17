using System.Runtime.InteropServices;
using System.Text;

namespace ArsanGazERP.Services;

/// <summary>
/// Sends raw bytes (TSPL) to a Windows printer spooler. Used when the API runs on Windows
/// with the TSC installed locally; Linux deployments use the browser + TsplPrintAgent instead.
/// </summary>
internal static class RawPrinter
{
    internal static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    internal static IReadOnlyList<string> ListPrinterNames()
    {
        if (!IsWindows)
        {
            return [];
        }

        // Prefer the known TSC name; full enumeration needs System.Drawing.Common which we avoid.
        return ["TSC TA310"];
    }

    internal static (bool Ok, string? Error) SendUtf8(string printerName, string payload)
    {
        if (!IsWindows)
        {
            return (false, "Sunucu Windows değil; TSPL'yi tarayıcıdaki yerel yazdırma ajanına veya .tspl dosyasına gönderin.");
        }

        string name = (printerName ?? "").Trim();
        if (name.Length == 0)
        {
            return (false, "Yazıcı adı boş.");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(payload ?? "");
        if (bytes.Length == 0)
        {
            return (false, "Gönderilecek TSPL boş.");
        }

        return SendBytes(name, bytes);
    }

    private static (bool Ok, string? Error) SendBytes(string printerName, byte[] bytes)
    {
        if (!OpenPrinter(printerName, out IntPtr handle, IntPtr.Zero))
        {
            return (false, $"Yazıcı açılamadı: {printerName}");
        }

        try
        {
            DOCINFOA di = new() { pDocName = "ArsanGazERP TSPL", pDataType = "RAW" };
            if (!StartDocPrinter(handle, 1, di))
            {
                return (false, "Yazıcı belgesi başlatılamadı (RAW).");
            }

            try
            {
                if (!StartPagePrinter(handle))
                {
                    return (false, "Yazıcı sayfası başlatılamadı.");
                }

                try
                {
                    IntPtr unmanaged = Marshal.AllocHGlobal(bytes.Length);
                    try
                    {
                        Marshal.Copy(bytes, 0, unmanaged, bytes.Length);
                        if (!WritePrinter(handle, unmanaged, bytes.Length, out int written) || written != bytes.Length)
                        {
                            return (false, "TSPL yazıcı kuyruğuna yazılamadı.");
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(unmanaged);
                    }
                }
                finally
                {
                    EndPagePrinter(handle);
                }
            }
            finally
            {
                EndDocPrinter(handle);
            }

            return (true, null);
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private class DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string? pDocName;
        [MarshalAs(UnmanagedType.LPStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPStr)] public string? pDataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] DOCINFOA di);

    [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);
}
