namespace EventFlowMcp.Abstractions.Rag;

public sealed record RagResult(
    string Id,
    string Source,
    string Title,
    string Text,
    double? Score = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
