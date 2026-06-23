using EventFlowMcp.Abstractions.Operations;
using EventFlowMcp.Abstractions.Rag;
using EventFlowMcp.Core.Operations;
using EventFlowMcp.Core.Rag;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EventFlowMcp.Core;

public static class EventFlowMcpServiceCollectionExtensions
{
    public static IServiceCollection AddEventFlowMcpCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var operationsProvider = configuration["Operations:Provider"] ?? "InMemory";
        if (!string.Equals(operationsProvider, "InMemory", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(operationsProvider, "ServiceControl", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(operationsProvider, "LocalProjection", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(operationsProvider, "PostgresProjection", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Operations provider '{operationsProvider}' is not registered. " +
                "Use InMemory, LocalProjection, PostgresProjection, or ServiceControl, or register an INServiceBusOperationsReader implementation.");
        }

        // A safe fallback makes the MCP usable locally without ServiceControl and without production data.
        // A host can replace this registration with a version-pinned ServiceControl reader.
        services.TryAddSingleton<INServiceBusOperationsReader, InMemoryNServiceBusOperationsReader>();

        var ragProvider = configuration["Rag:Provider"];
        if (string.Equals(ragProvider, "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IRagRetriever, InMemoryRagRetriever>();
        }
        else
        {
            services.AddSingleton<IRagRetriever, NoOpRagRetriever>();
        }

        return services;
    }
}
