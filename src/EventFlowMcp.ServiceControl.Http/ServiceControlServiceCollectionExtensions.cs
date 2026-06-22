using EventFlowMcp.Abstractions.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EventFlowMcp.ServiceControl.Http;

public static class ServiceControlServiceCollectionExtensions
{
    public static IServiceCollection AddServiceControlOperationsReader(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ServiceControlOptions>(configuration.GetSection("Operations:ServiceControl"));

        services.AddHttpClient<ServiceControlHttpOperationsReader>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<ServiceControlOptions>>().Value;
            if (!Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out var apiBaseUri))
                throw new InvalidOperationException("Operations:ServiceControl:ApiBaseUrl must be an absolute URI.");

            client.BaseAddress = apiBaseUri.AbsoluteUri.EndsWith('/')
                ? apiBaseUri
                : new Uri($"{apiBaseUri.AbsoluteUri}/", UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(15);

            if (!string.IsNullOrWhiteSpace(options.BearerToken))
                client.DefaultRequestHeaders.Authorization = new("Bearer", options.BearerToken);
        });

        // Register after the in-memory fallback from EventFlowMcp.Core.
        services.AddTransient<INServiceBusOperationsReader>(provider =>
            provider.GetRequiredService<ServiceControlHttpOperationsReader>());

        return services;
    }
}
