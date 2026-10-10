using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.AgentWatcher;

internal static class WorkflowEvidenceVerifier
{
    internal static string? GetTestRunReference(
        WorkflowJob job,
        WorkflowCriterion criterion,
        LocalTestAutomationStatus status)
    {
        if (criterion.ValidatorId != WorkflowOrchestrator.AgentWatcherRegressionValidator
            || !IsFreshSuccessfulLocalTest(job, status)
            || status.LastRunStartedUtc is not { } started)
        {
            return null;
        }

        return "agentwatcher-regression-" + started.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static string? GetSupervisorHealthReference(
        WorkflowJob job,
        WorkflowCriterion criterion,
        string reportPath)
    {
        if (criterion.ValidatorId != WorkflowOrchestrator.SupervisorHealthValidator
            || string.IsNullOrWhiteSpace(criterion.ValidationTarget)
            || job.ResultReceivedUtc is not { } resultUtc)
        {
            return null;
        }

        try
        {
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
            if (!report.RootElement.TryGetProperty("CheckedAtLocal", out JsonElement checkedElement)
                || checkedElement.ValueKind != JsonValueKind.String
                || !report.RootElement.TryGetProperty("Checks", out JsonElement checks)
                || checks.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            DateTime checkedLocal = checkedElement.GetDateTime();
            DateTimeOffset checkedUtc = checkedLocal.Kind switch
            {
                DateTimeKind.Utc => new DateTimeOffset(checkedLocal),
                DateTimeKind.Local => new DateTimeOffset(checkedLocal),
                _ => new DateTimeOffset(DateTime.SpecifyKind(checkedLocal, DateTimeKind.Local))
            };
            if (checkedUtc <= new DateTimeOffset(DateTime.SpecifyKind(resultUtc, DateTimeKind.Utc)))
            {
                return null;
            }

            bool isHealthy = checks.EnumerateArray().Any(check =>
                check.TryGetProperty("Name", out JsonElement name)
                && name.ValueKind == JsonValueKind.String
                && string.Equals(name.GetString(), criterion.ValidationTarget, StringComparison.Ordinal)
                && check.TryGetProperty("IsHealthy", out JsonElement healthy)
                && healthy.ValueKind == JsonValueKind.True);
            if (!isHealthy)
            {
                return null;
            }

            return CreateHealthReference(criterion.ValidationTarget, checkedUtc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    internal static bool Verify(
        WorkflowJob job,
        WorkflowCriterion criterion,
        WorkflowCriterionProof proof,
        LocalTestAutomationStatus localTestStatus,
        string supervisorReportPath)
    {
        if (!string.Equals(proof.EvidenceKind, "TestRun", StringComparison.Ordinal))
        {
            return false;
        }

        return criterion.ValidatorId switch
        {
            WorkflowOrchestrator.AgentWatcherRegressionValidator =>
                string.Equals(
                    proof.EvidenceReference,
                    GetTestRunReference(job, criterion, localTestStatus),
                    StringComparison.Ordinal),
            WorkflowOrchestrator.SupervisorHealthValidator =>
                string.Equals(
                    proof.EvidenceReference,
                    GetSupervisorHealthReference(job, criterion, supervisorReportPath),
                    StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool IsFreshSuccessfulLocalTest(WorkflowJob job, LocalTestAutomationStatus status) =>
        status.LastOutcome == "Passed"
        && status.LastRunStartedUtc is { } started
        && job.ResultReceivedUtc is { } resultReceived
        && started.ToUniversalTime() > new DateTimeOffset(DateTime.SpecifyKind(resultReceived, DateTimeKind.Utc))
        && status.Report.StartsWith("Yerel test paketi başarıyla tamamlandı.", StringComparison.Ordinal);

    private static string CreateHealthReference(string checkName, DateTimeOffset checkedUtc)
    {
        string payload = checkName + "\n" + checkedUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16];
        return "supervisor-health-" + hash;
    }
}
