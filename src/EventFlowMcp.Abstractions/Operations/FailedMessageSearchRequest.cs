namespace EventFlowMcp.Abstractions.Operations;

public sealed record FailedMessageSearchRequest(
    int MaxResults = 10,
    string? Endpoint = null,
    string? MessageType = null,
    string? ExceptionType = null,
    DateTimeOffset? Since = null);
