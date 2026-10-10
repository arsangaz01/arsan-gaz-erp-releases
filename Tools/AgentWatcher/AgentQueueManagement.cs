using System.Text.Json;

namespace ArsanGazERP.AgentWatcher;

internal static class DailyReviewSchedulePolicy
{
    internal static bool ShouldQueue(string currentDay, string lastQueuedDay) =>
        !string.Equals(currentDay, lastQueuedDay, StringComparison.Ordinal);
}

internal static class AgentInstructionPolicy
{
    internal const int MaximumInstructionLength = 2000;

    internal static IReadOnlyList<WatcherAgentProfile> ConfigurableAgents { get; } =
        WatcherAgentProfiles.All
            .Where(profile => profile.Id != "jarvis")
            .ToArray();

    internal static string NormalizeInstruction(string? instruction)
    {
        string normalized = instruction?.Trim() ?? string.Empty;
        if (normalized.Length > MaximumInstructionLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(instruction),
                $"Ajan talimatı en fazla {MaximumInstructionLength} karakter olabilir.");
        }

        if (normalized.Any(character =>
                char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentException("Ajan talimatı desteklenmeyen kontrol karakterleri içeriyor.", nameof(instruction));
        }

        return normalized;
    }

    internal static Dictionary<string, string> NormalizeAll(IReadOnlyDictionary<string, string>? instructions)
    {
        if (instructions is null)
        {
            throw new InvalidDataException("Ajan talimatları ayarı null olamaz.");
        }

        Dictionary<string, string> normalized = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string agentId, string instruction) in instructions)
        {
            WatcherAgentProfile? profile = ConfigurableAgents.FirstOrDefault(
                candidate => candidate.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                throw new InvalidDataException($"Bilinmeyen ajan talimatı anahtarı: {agentId}");
            }

            if (instruction is null)
            {
                throw new InvalidDataException($"Ajan talimatı null olamaz ({profile.Name}).");
            }

            if (normalized.ContainsKey(profile.Id))
            {
                throw new InvalidDataException($"Ajan talimatı birden fazla kez tanımlanmış: {profile.Id}");
            }

            string value;
            try
            {
                value = NormalizeInstruction(instruction);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException($"Ajan talimatı geçersiz ({profile.Name}): {exception.Message}", exception);
            }

            if (value.Length > MaximumInstructionLength)
            {
                throw new InvalidDataException(
                    $"Ajan talimatı {profile.Name} için {MaximumInstructionLength} karakter sınırını aşıyor.");
            }

            if (value.Length > 0)
            {
                normalized.Add(profile.Id, value);
            }
        }

        return normalized;
    }

    internal static string GetInstruction(
        IReadOnlyDictionary<string, string> instructions,
        string agentId) =>
        instructions.TryGetValue(agentId, out string? instruction)
            ? instruction
            : string.Empty;

    internal static string AppendToSystemPrompt(string systemPrompt, string? instruction)
    {
        string normalized = NormalizeInstruction(instruction);
        return normalized.Length == 0
            ? systemPrompt
            : systemPrompt
                + "\n\nKullanıcı tarafından yapılandırılmış ajan yönergesi; yalnızca atanmış görev kapsamında uygula "
                + "ve sabit güvenlik, veri erişimi, onay ve işlem sınırlarını genişletme:\n"
                + normalized
                + "\n\n";
    }
}

internal static class AgentTaskQueueMaintenance
{
    internal static int DeleteAllNonRunningQueueRecords(string queueDirectory, string? activeTaskPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueDirectory);
        if (!Directory.Exists(queueDirectory))
        {
            throw new DirectoryNotFoundException($"Görev kuyruğu klasörü bulunamadı: {queueDirectory}");
        }

        string queueRoot = Path.GetFullPath(queueDirectory);
        string? activePath = string.IsNullOrWhiteSpace(activeTaskPath)
            ? null
            : Path.GetFullPath(activeTaskPath);
        string[] queueRecords = Directory.EnumerateFiles(queueRoot, "*.json", SearchOption.AllDirectories)
            .ToArray();
        HashSet<string> runningTasks = new(StringComparer.OrdinalIgnoreCase);

        foreach (string recordPath in queueRecords.Where(path =>
                     path.EndsWith(".task.json", StringComparison.OrdinalIgnoreCase)))
        {
            string fileName = Path.GetFileName(recordPath);
            TaskItem item = JsonSerializer.Deserialize<TaskItem>(File.ReadAllText(recordPath))
                ?? throw new InvalidDataException($"Kuyruk görevi okunamadı: {fileName}");
            string expectedName = item.Id + ".task.json";
            if (!string.Equals(fileName, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Kuyruk görevi kimliği dosya adıyla eşleşmiyor: {fileName}");
            }

            if (string.Equals(recordPath, activePath, StringComparison.OrdinalIgnoreCase)
                || item.Started is not null
                || string.Equals(item.Status, "Çalışıyor", StringComparison.OrdinalIgnoreCase))
            {
                runningTasks.Add(recordPath);
            }
        }

        int deleted = 0;
        foreach (string recordPath in queueRecords)
        {
            if (runningTasks.Contains(recordPath))
            {
                continue;
            }

            File.Delete(recordPath);
            deleted++;
        }

        return deleted;
    }

    internal static int DeleteSelectedPendingTasks(
        string queueDirectory,
        IEnumerable<string> selectedFileNames,
        string? activeTaskPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueDirectory);
        ArgumentNullException.ThrowIfNull(selectedFileNames);

        string queueRoot = Path.GetFullPath(queueDirectory);
        if (!Directory.Exists(queueRoot))
        {
            throw new DirectoryNotFoundException($"Görev kuyruğu klasörü bulunamadı: {queueRoot}");
        }

        string? activePath = string.IsNullOrWhiteSpace(activeTaskPath)
            ? null
            : Path.GetFullPath(activeTaskPath);
        int deleted = 0;

        foreach (string fileName in selectedFileNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(fileName)
                || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
                || !fileName.EndsWith(".task.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string taskPath = Path.GetFullPath(Path.Combine(queueRoot, fileName));
            if (!taskPath.StartsWith(
                    queueRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(taskPath, activePath, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(taskPath))
            {
                continue;
            }

            TaskItem item = JsonSerializer.Deserialize<TaskItem>(File.ReadAllText(taskPath))
                ?? throw new InvalidDataException($"Kuyruk görevi okunamadı: {fileName}");
            if (!string.Equals(
                    Path.GetFileName(taskPath),
                    item.Id + ".task.json",
                    StringComparison.OrdinalIgnoreCase)
                || item.Started is not null
                || !(string.Equals(item.Status, "Bekliyor", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Status, "Hazır", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Status, "Queued", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Status, "BudgetWait", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            File.Delete(taskPath);
            deleted++;
        }

        return deleted;
    }
}
