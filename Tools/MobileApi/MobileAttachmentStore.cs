using ArsanGazERP.Services;

namespace ArsanGazERP.MobileApi;

internal sealed record SavedMobileAttachment(string FileName, string LocalPath, string Kind);

internal static class MobileAttachmentStore
{
    internal const long MaxFileBytes = 15 * 1024 * 1024;
    internal const int MaxFilesPerRequest = 5;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".pdf", ".mp4", ".mov", ".m4v", ".3gp"
    };

    internal static string Root => Path.Combine(AgentWatcherPaths.DataRoot, "mobile-attachments");

    internal static async Task<IReadOnlyList<SavedMobileAttachment>> SaveAsync(
        string requestId,
        IFormFileCollection files,
        CancellationToken cancellationToken)
    {
        if (files.Count is 0 or > MaxFilesPerRequest)
        {
            throw new ArgumentException($"En fazla {MaxFilesPerRequest} ek gönderilebilir.");
        }

        string directory = Path.Combine(Root, requestId);
        Directory.CreateDirectory(directory);
        List<SavedMobileAttachment> saved = [];

        foreach (IFormFile file in files)
        {
            if (file.Length is 0 or > MaxFileBytes)
            {
                throw new ArgumentException($"Her ek en fazla {MaxFileBytes / (1024 * 1024)} MB olabilir.");
            }

            string extension = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(extension) || !AllowedExtensions.Contains(extension))
            {
                throw new ArgumentException("Desteklenmeyen dosya türü. Fotoğraf, PDF veya video seçin.");
            }

            string safeName = Guid.NewGuid().ToString("N")[..12] + extension.ToLowerInvariant();
            string fullPath = Path.GetFullPath(Path.Combine(directory, safeName));
            if (!fullPath.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Dosya yolu geçersiz.");
            }

            await using (FileStream stream = new(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }

            saved.Add(new SavedMobileAttachment(
                Path.GetFileName(file.FileName).Length > 0 ? Path.GetFileName(file.FileName) : safeName,
                fullPath,
                Classify(extension)));
        }

        return saved;
    }

    private static string Classify(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => "document",
        ".mp4" or ".mov" or ".m4v" or ".3gp" => "video",
        _ => "photo"
    };
}
