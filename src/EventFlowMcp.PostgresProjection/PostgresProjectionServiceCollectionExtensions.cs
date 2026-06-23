using EventFlowMcp.Abstractions.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EventFlowMcp.PostgresProjection;

public static class PostgresProjectionServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresProjectionOperationsReader(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration["Operations:PostgresProjection:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Operations:PostgresProjection:ConnectionString is required when Operations:Provider is PostgresProjection.");
        }

        services.AddSingleton(_ => new PostgresOperationsProjection(connectionString));
        // Register after EventFlowMcp.Core's in-memory fallback so this reader is selected.
        services.AddSingleton<INServiceBusOperationsReader>(provider =>
            provider.GetRequiredService<PostgresOperationsProjection>());
        return services;
    }
}
