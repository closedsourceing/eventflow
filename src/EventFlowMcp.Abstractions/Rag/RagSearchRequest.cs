namespace EventFlowMcp.Abstractions.Rag;

public sealed record RagSearchRequest(
    string Query,
    int MaxResults = 5,
    double? MinimumScore = null,
    IReadOnlyDictionary<string, string>? Filters = null);
