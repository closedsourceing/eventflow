namespace EventFlowMcp.ServiceControl.Http;

/// <summary>
/// Settings for the ServiceControl HTTP API adapter. This adapter is intentionally
/// version-pinned by the consuming team because ServiceControl does not guarantee a
/// public, stable third-party HTTP API contract.
/// </summary>
public sealed class ServiceControlOptions
{
    /// <summary>
    /// ServiceControl API root. Most installations use a URL ending in /api/;
    /// installations with integrated ServicePulse can expose the API at the root instead.
    /// </summary>
    public string ApiBaseUrl { get; set; } = "http://localhost:33333/api/";

    /// <summary>Optional bearer token for a ServiceControl installation protected by an upstream gateway.</summary>
    public string? BearerToken { get; set; }

    /// <summary>Maximum items requested in one ServiceControl page.</summary>
    public int PageSize { get; set; } = 100;
}
