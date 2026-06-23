using EventFlowMcp.Abstractions.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EventFlowMcp.LocalProjection;

public static class LocalProjectionServiceCollectionExtensions
{
    public static IServiceCollection AddLocalProjectionOperationsReader(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var projectionPath = configuration["Operations:LocalProjection:Path"];
        if (string.IsNullOrWhiteSpace(projectionPath))
        {
            throw new InvalidOperationException(
                "Operations:LocalProjection:Path is required when Operations:Provider is LocalProjection.");
        }

        // Register after EventFlowMcp.Core's in-memory fallback so this reader is selected.
        services.AddSingleton<INServiceBusOperationsReader>(_ => new LocalOperationsProjection(projectionPath));
        return services;
    }
}
