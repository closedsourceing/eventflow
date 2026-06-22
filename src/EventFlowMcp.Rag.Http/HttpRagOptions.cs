namespace EventFlowMcp.Rag.Http;

public sealed class HttpRagOptions
{
    public string Endpoint { get; set; } = "http://localhost:5050";
    public string SearchPath { get; set; } = "/rag/search";
    public string? ApiKey { get; set; }
    public string ApiKeyHeaderName { get; set; } = "X-RAG-API-Key";
}
