using System.Collections.Generic;

namespace ArsanGazERP.Models;

public sealed class MultiServerOptions
{
    public string ApiKeyEnvironmentVariable { get; set; } = "ARSANGAZ_ERP_API_KEY";
    public int FailureCooldownSeconds { get; set; } = 30;
    public List<ServerEndpoint> Servers { get; set; } = new();
}
