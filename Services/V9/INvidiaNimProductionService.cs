using System.Threading;
using System.Threading.Tasks;

namespace ArsanGazERP.Services.V9;

public interface INvidiaNimProductionService
{
    Task<NvidiaNimHealth> CheckHealthAsync(CancellationToken cancellationToken = default);
    Task<NvidiaNimResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
}
