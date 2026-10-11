using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArsanGazERP.MobileApi;

internal sealed record MobileApkVersion(string VersionName, int VersionCode);

/// <summary>
/// Sunucunun duyurduğu mobil sürüm, indirilen APK ile aynı olmalı; aksi halde güncelleme döngüsü oluşur.
/// </summary>
internal static class MobileApkPublishInfo
{
    private static string MetaFile => MobileApiPaths.ApkFile + ".meta.json";
    private static readonly Lock CacheGate = new();
    private static (long Length, DateTime WrittenUtc, MobileApkVersion Version)? cachedApkVersion;

    internal static MobileApkVersion ResolvePublishedVersion()
    {
        if (File.Exists(MobileApiPaths.ApkFile))
        {
            MobileApkVersion? fromApk = ReadApkVersionCached();
            if (fromApk is not null)
            {
                return fromApk;
            }

            MobileApkVersion? fromMeta = TryReadMeta();
            if (fromMeta is not null)
            {
                return fromMeta;
            }
        }

        return new MobileApkVersion(MobileReleaseInfo.PublishedMobileVersion, MobileReleaseInfo.PublishedMobileBuild);
    }

    internal static void WriteMeta(string versionName, int versionCode) =>
        TryWriteMeta(new MobileApkVersion(versionName, versionCode));

    private static MobileApkVersion? TryReadMeta()
    {
        if (!File.Exists(MetaFile))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(MetaFile));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("versionName", out JsonElement nameElement)
                || !root.TryGetProperty("versionCode", out JsonElement codeElement))
            {
                return null;
            }

            string name = nameElement.GetString() ?? string.Empty;
            int code = codeElement.ValueKind == JsonValueKind.Number
                ? codeElement.GetInt32()
                : int.Parse(codeElement.GetString() ?? "0");
            return name.Length > 0 && code > 0 ? new MobileApkVersion(name, code) : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or FormatException)
        {
            MobileLog.Write("APK meta okunamadı: " + exception.Message);
            return null;
        }
    }

    // /api/merhaba and /api/guncelleme are unauthenticated; aapt runs only when the APK file changes.
    private static MobileApkVersion? ReadApkVersionCached()
    {
        FileInfo apk = new(MobileApiPaths.ApkFile);
        lock (CacheGate)
        {
            if (cachedApkVersion is { } cached && cached.Length == apk.Length && cached.WrittenUtc == apk.LastWriteTimeUtc)
            {
                return cached.Version;
            }

            MobileApkVersion? version = TryReadFromApkFile();
            if (version is not null)
            {
                cachedApkVersion = (apk.Length, apk.LastWriteTimeUtc, version);
                TryWriteMeta(version);
            }

            return version;
        }
    }

    private static MobileApkVersion? TryReadFromApkFile()
    {
        string? aapt = FindAapt();
        if (aapt is null)
        {
            return null;
        }

        try
        {
            ProcessStartInfo start = new(aapt, $"dump badging \"{MobileApiPaths.ApkFile}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("aapt başlatılamadı.");
            Task<string> error = process.StandardError.ReadToEndAsync();
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("aapt 15 saniyede yanıt vermedi.");
            }

            string output = standardOutput.GetAwaiter().GetResult();
            _ = error.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                return null;
            }

            Match codeMatch = Regex.Match(output, @"versionCode='(\d+)'");
            Match nameMatch = Regex.Match(output, @"versionName='([^']+)'");
            if (!codeMatch.Success || !nameMatch.Success)
            {
                return null;
            }

            return new MobileApkVersion(nameMatch.Groups[1].Value, int.Parse(codeMatch.Groups[1].Value));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException
                                              or System.ComponentModel.Win32Exception or FormatException or OverflowException)
        {
            MobileLog.Write("APK badging okunamadı: " + exception.Message);
            return null;
        }
    }

    private static void TryWriteMeta(MobileApkVersion version)
    {
        try
        {
            string json = JsonSerializer.Serialize(new { versionName = version.VersionName, versionCode = version.VersionCode });
            File.WriteAllText(MetaFile, json);
        }
        catch (IOException exception)
        {
            MobileLog.Write("APK meta yazılamadı: " + exception.Message);
        }
    }

    private static string? FindAapt()
    {
        List<string> roots = new();
        string? androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME");
        string? androidSdk = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        if (!string.IsNullOrWhiteSpace(androidHome))
        {
            roots.Add(androidHome);
        }

        if (!string.IsNullOrWhiteSpace(androidSdk))
        {
            roots.Add(androidSdk);
        }

        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "sdk"));

        foreach (string? root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string buildTools = Path.Combine(root, "build-tools");
            if (!Directory.Exists(buildTools))
            {
                continue;
            }

            foreach (string folder in Directory.GetDirectories(buildTools).OrderDescending(StringComparer.Ordinal))
            {
                string candidate = Path.Combine(folder, OperatingSystem.IsWindows() ? "aapt.exe" : "aapt");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
