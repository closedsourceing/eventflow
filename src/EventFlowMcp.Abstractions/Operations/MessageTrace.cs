namespace EventFlowMcp.Abstractions.Operations;

public sealed record MessageTrace(
    string ConversationId,
    IReadOnlyList<MessageActivity> Activities,
    IReadOnlyDictionary<string, string>? Correlation = null,
    string? Summary = null);
