using EventFlowMcp.Abstractions.Rag;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EventFlowMcp.Rag.Http;

public static class HttpRagServiceCollectionExtensions
{
    public static IServiceCollection AddHttpRagRetriever(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<HttpRagOptions>(configuration.GetSection("Rag:Http"));

        services.AddHttpClient<IRagRetriever, HttpRagRetriever>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<HttpRagOptions>>().Value;
            client.BaseAddress = new Uri(options.Endpoint);

            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                client.DefaultRequestHeaders.Add(options.ApiKeyHeaderName, options.ApiKey);
        });

        return services;
    }
}
