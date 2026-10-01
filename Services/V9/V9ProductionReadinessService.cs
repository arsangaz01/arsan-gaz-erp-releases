using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArsanGazERP.Services.V9;

public sealed record V9ReadinessReport(
    string ApplicationVersion,
    NvidiaNimHealth Nvidia,
    bool Microsoft365StatePresent,
    bool DatabasePresent,
    bool AgentWatcherPresent,
    DateTimeOffset CheckedAtUtc);

public sealed class V9ProductionReadinessService
{
    public async Task<V9ReadinessReport> InspectAsync(CancellationToken cancellationToken = default)
    {
        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArsanGazERP");
        string dataDirectory = Path.Combine(local, "Data");

        using NvidiaNimProductionService nvidiaService = new();
        NvidiaNimHealth nvidia = await nvidiaService.CheckHealthAsync(cancellationToken);

        return new V9ReadinessReport(
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "9.0.0",
            nvidia,
            File.Exists(Path.Combine(local, "connection-state.json")),
            Directory.Exists(dataDirectory) && Directory.EnumerateFiles(dataDirectory, "*.db").Any(),
            File.Exists(Path.Combine(AppContext.BaseDirectory, "AgentWatcher", "ArsanGazERP.AgentWatcher.exe")),
            DateTimeOffset.UtcNow);
    }

    public static async Task<string> WriteReportAsync(CancellationToken cancellationToken = default)
    {
        V9ReadinessReport report = await new V9ProductionReadinessService().InspectAsync(cancellationToken);
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArsanGazERP",
            "Reports");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "v9-production-readiness.json");
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken);
        return path;
    }
}
