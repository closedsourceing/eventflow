using System.Net.Http.Json;
using EventFlowMcp.Abstractions.Rag;
using Microsoft.Extensions.Options;

namespace EventFlowMcp.Rag.Http;

public sealed class HttpRagRetriever : IRagRetriever
{
    private readonly HttpClient _httpClient;
    private readonly HttpRagOptions _options;

    public HttpRagRetriever(HttpClient httpClient, IOptions<HttpRagOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<RagResult>> SearchAsync(
        RagSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(_options.SearchPath, request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<List<RagResult>>(cancellationToken: cancellationToken);
        return results ?? [];
    }
}
