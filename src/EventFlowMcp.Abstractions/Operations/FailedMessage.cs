namespace EventFlowMcp.Abstractions.Operations;

public sealed record FailedMessage(
    string Id,
    string MessageId,
    string ConversationId,
    string Endpoint,
    string MessageType,
    string ExceptionType,
    string ExceptionMessage,
    DateTimeOffset FailedAt,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? BodyPreview = null);
