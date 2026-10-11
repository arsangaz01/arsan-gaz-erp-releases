using System.IO;
using System.Security.Cryptography;

namespace ArsanGazERP.Services;

// Windows: DPAPI (CurrentUser). Linux server: DPAPI does not exist, so secret files hold plain bytes and are
// restricted to the service user (0600). A Windows DPAPI blob copied to Linux cannot be opened and is reported as
// CryptographicException so callers treat the feature as not configured instead of crashing.
internal static class LocalDataProtector
{
    public static byte[] Protect(byte[] clearBytes, byte[]? entropy = null) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Protect(clearBytes, entropy, DataProtectionScope.CurrentUser)
            : clearBytes;

    public static byte[] Unprotect(byte[] storedBytes, byte[]? entropy = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return ProtectedData.Unprotect(storedBytes, entropy, DataProtectionScope.CurrentUser);
        }

        // DPAPI blobs start with the version DWORD 01 00 00 00; plain secrets are text.
        if (storedBytes.Length >= 4 && storedBytes[0] == 1 && storedBytes[1] == 0 && storedBytes[2] == 0 && storedBytes[3] == 0)
        {
            throw new CryptographicException("Windows DPAPI ile şifrelenmiş anahtar bu sunucuda açılamaz; anahtarı sunucuda yeniden kaydedin.");
        }

        return storedBytes;
    }

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, bytes);
            return;
        }

        using (FileStream stream = new(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        }))
        {
            stream.Write(bytes);
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
