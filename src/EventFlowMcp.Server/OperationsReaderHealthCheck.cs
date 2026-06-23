using EventFlowMcp.Abstractions.Operations;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EventFlowMcp.Server;

/// <summary>Readiness verifies that the configured read-only operations source responds.</summary>
public sealed class OperationsReaderHealthCheck(INServiceBusOperationsReader operations) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await operations.GetEndpointHealthAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("The configured operations reader responded.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The configured operations reader did not respond.", exception);
        }
    }
}
