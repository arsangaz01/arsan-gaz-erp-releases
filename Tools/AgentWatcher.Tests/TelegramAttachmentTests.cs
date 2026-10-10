using System.Net;
using System.Text;
using System.Text.Json;
using ArsanGazERP.AgentWatcher;

internal static class TelegramAttachmentTests
{
    public static async Task RunAsync()
    {
        await CheckAsync(authorized: true, remotePath: "documents/file_1.pdf", expectedFiles: 1);
        await CheckAsync(authorized: false, remotePath: "documents/file_1.pdf", expectedFiles: 0);
        await CheckAsync(
            authorized: true, remotePath: "photos/image_1.jpg", expectedFiles: 1,
            attachmentKind: "photo", expectedFileName: "image.jpg");
        await CheckAsync(
            authorized: true, remotePath: "photos/image_1.jpg", expectedFiles: 1,
            attachmentKind: "photo", expectedCaption: "Ekteki dosyayı incele.",
            includeCaption: false, expectedFileName: "image.jpg");
        foreach (string photoShape in new[] { "wrong-type", "empty", "non-object", "missing-file-id" })
            await CheckAsync(
                authorized: true, remotePath: "photos/image_1.jpg", expectedFiles: 0, expectError: true,
                attachmentKind: "photo", photoShape: photoShape);
        foreach (string remotePath in new[] { "../file.pdf", "documents/%2e%2e/file.pdf", "documents/file.pdf?token=x", "/file.pdf" })
            await CheckAsync(authorized: true, remotePath, expectedFiles: 0, expectError: true);
        await CheckAsync(authorized: true, "documents/file.pdf", expectedFiles: 0, expectError: true, declaredSize: 0);
        await CheckAsync(authorized: true, "documents/file.pdf", expectedFiles: 0, expectError: true, declaredSize: 3);
        await CheckAsync(authorized: true, "documents/file.pdf", expectedFiles: 0, expectError: true, declaredSize: 20 * 1024 * 1024 + 1);
        await CheckAsync(authorized: true, "documents/file.pdf", expectedFiles: 0, expectError: true, declaredSize: "four");
        await CheckAsync(
            authorized: true, "documents/file.pdf", expectedFiles: 0, expectError: true,
            caption: 42);
        await CheckAsync(authorized: true, "documents/file.pdf", expectedFiles: 0, expectError: true, empty: true);
        await CheckAsync(authorized: true, "documents/file.pdf", expectedFiles: 0, expectError: true, failDownload: true);
        await CheckAlbumBecomesOneRequestAsync();
        Console.WriteLine("PASS: Telegram document/photo, album grouping, malformed metadata/caption, invalid path, download failure and cleanup regressions (mock only).");
    }

    static async Task CheckAlbumBecomesOneRequestAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "telegram-album-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using CancellationTokenSource stop = new();
            using AlbumHandler handler = new(stop);
            using TelegramBotService bot = new("fake-token", 7, handler, Path.Combine(root, "offset.txt"));
            List<(long UpdateId, string Caption, int Files)> calls = [];
            await bot.RunAsync(
                (_, _, _, _) => throw new InvalidOperationException("Album must not become a plain text command."),
                (_, _, _, _) => Task.FromResult(""),
                _ => { }, stop.Token,
                (updateId, caption, _, attachments, _) =>
                {
                    calls.Add((updateId, caption, attachments.Count));
                    return Task.FromResult("");
                });
            if (calls.Count != 1 || calls[0] != (200, "Asıl istek", 3))
                throw new InvalidOperationException(
                    "Album was not merged into one request: " + string.Join(" | ", calls));
            if (File.ReadAllText(Path.Combine(root, "offset.txt")).Trim() != "203")
                throw new InvalidOperationException("Album offset was not advanced past the last album update.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    sealed class AlbumHandler(CancellationTokenSource stop) : HttpMessageHandler
    {
        int polls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/getUpdates", StringComparison.Ordinal))
            {
                polls++;
                if (polls == 1)
                    return Task.FromResult(Json(new { ok = true, result = new[] { Photo(200, null), Photo(201, "Asıl istek") } }));
                if (polls == 2)
                    return Task.FromResult(Json(new { ok = true, result = new[] { Photo(202, null) } }));
                stop.Cancel();
                throw new OperationCanceledException(stop.Token);
            }
            if (route.EndsWith("/getFile", StringComparison.Ordinal))
                return Task.FromResult(Json(new { ok = true, result = new { file_path = "photos/image_1.jpg" } }));
            if (route.EndsWith("/sendMessage", StringComparison.Ordinal))
                return Task.FromResult(Json(new { ok = true, result = new { message_id = 1 } }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("test")) });
        }

        static object Photo(long updateId, string? caption) => new
        {
            update_id = updateId,
            message = new Dictionary<string, object?>
            {
                ["chat"] = new { id = 7 },
                ["media_group_id"] = "album-1",
                ["caption"] = caption,
                ["photo"] = new object[] { new { file_id = "file-" + updateId, file_size = 4 } }
            }
        };

        static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }

    static async Task CheckAsync(
        bool authorized, string remotePath, int expectedFiles, bool expectError = false,
        object? declaredSize = null, bool empty = false, bool failDownload = false,
        string attachmentKind = "document", string photoShape = "valid",
        object? caption = null, bool includeCaption = true,
        string expectedCaption = "  Exact caption\n", string expectedFileName = "offer.pdf")
    {
        string root = Path.Combine(Path.GetTempPath(), "telegram-attachment-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using CancellationTokenSource stop = new();
            using AttachmentHandler handler = new(
                stop, authorized, remotePath, declaredSize ?? 4, empty, failDownload,
                attachmentKind, photoShape, caption ?? "  Exact caption\n", includeCaption);
            using TelegramBotService bot = new("fake-token", 7, handler, Path.Combine(root, "offset.txt"));
            int files = 0;
            List<string> logs = [];
            await bot.RunAsync(
                (_, _, _, _) => throw new InvalidOperationException("Document must not become a plain text command."),
                (_, _, _, _) => Task.FromResult(""),
                logs.Add, stop.Token,
                async (_, caption, chatId, attachments, cancellationToken) =>
                {
                    if (caption != expectedCaption || chatId != 7 || attachments.Count != 1)
                        throw new InvalidOperationException("Attachment routing or caption changed.");
                    TelegramAttachment file = attachments[0];
                    string? content = File.Exists(file.LocalPath)
                        ? await File.ReadAllTextAsync(file.LocalPath, cancellationToken) : null;
                    if (file.Size != 4 || file.FileName != expectedFileName || content != "test")
                        throw new InvalidOperationException(
                            $"Downloaded file metadata/content changed: {file.Size}, {file.FileName}, expected {expectedFileName}, {content}.");
                    files++;
                    return "";
                });
            if (files != expectedFiles || handler.Notices.Count != (expectError ? 1 : 0))
                throw new InvalidOperationException(
                    $"Attachment acceptance/error notice count is incorrect for {attachmentKind}/{photoShape}: "
                    + $"{files} accepted, {handler.Notices.Count} notices. {string.Join(" | ", logs)}");
            if (!authorized && handler.DownloadCalls != 0)
                throw new InvalidOperationException("Unauthorized attachment triggered network access.");
            if (Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any())
                throw new InvalidOperationException("Failed download retained a temporary file.");
            if (expectError && !logs.Any(log => log.Contains("işlenemedi", StringComparison.Ordinal)))
                throw new InvalidOperationException("Attachment failure was not surfaced in the log.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    sealed class AttachmentHandler(
        CancellationTokenSource stop, bool authorized, string remotePath, object? size,
        bool empty, bool failDownload, string attachmentKind, string photoShape,
        object? caption, bool includeCaption) : HttpMessageHandler
    {
        int updates;
        public int DownloadCalls { get; private set; }
        public List<string> Notices { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/getUpdates", StringComparison.Ordinal))
            {
                if (updates++ != 0)
                {
                    stop.Cancel();
                    throw new OperationCanceledException(stop.Token);
                }
                Dictionary<string, object?> message = new()
                {
                    ["chat"] = new { id = authorized ? 7 : 8 }
                };
                if (includeCaption)
                    message["caption"] = caption;
                if (attachmentKind == "photo")
                {
                    message["photo"] = photoShape switch
                    {
                        "wrong-type" => "not-an-array",
                        "empty" => Array.Empty<object>(),
                        "non-object" => new object[] { "not-an-attachment" },
                        "missing-file-id" => new object[] { new { file_size = size } },
                        _ => new object[]
                        {
                            new { file_id = "small-photo", file_size = 1 },
                            new { file_id = "file", file_size = size }
                        }
                    };
                }
                else
                {
                    message["document"] = new
                    {
                        file_id = "file",
                        file_name = "offer.pdf",
                        file_size = size
                    };
                }
                return Json(new
                {
                    ok = true,
                    result = new[]
                    {
                        new
                        {
                            update_id = 123,
                            message
                        }
                    }
                });
            }
            if (route.EndsWith("/getFile", StringComparison.Ordinal))
            {
                DownloadCalls++;
                return Json(new { ok = true, result = new { file_path = remotePath } });
            }
            if (route.EndsWith("/sendMessage", StringComparison.Ordinal))
            {
                Notices.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json(new { ok = true, result = new { message_id = 1 } });
            }
            DownloadCalls++;
            return new HttpResponseMessage(failDownload ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(empty ? [] : Encoding.UTF8.GetBytes("test"))
            };
        }

        static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }
}
