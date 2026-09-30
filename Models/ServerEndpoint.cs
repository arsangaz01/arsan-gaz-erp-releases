using System;

namespace ArsanGazERP.Models;

public sealed class ServerEndpoint
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;
    public string HealthPath { get; set; } = "health";
    public int TimeoutSeconds { get; set; } = 15;

    public Uri GetBaseUri()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"Geçersiz sunucu adresi: {Name} - {BaseUrl}");
        }

        return new Uri(uri.ToString().TrimEnd('/') + "/");
    }
}

public sealed record ServerHealthResult(
    string Name,
    string BaseUrl,
    bool IsHealthy,
    int? StatusCode,
    long ElapsedMilliseconds,
    string Message);
