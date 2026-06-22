namespace EventFlowMcp.Abstractions.Operations;

public enum MessageIntent
{
    Unknown,
    Command,
    Event,
    Timeout,
    Reply
}

public enum MessageProcessingStatus
{
    Succeeded,
    Failed,
    Pending
}

public sealed record MessageActivity(
    string MessageId,
    string MessageType,
    MessageIntent Intent,
    string? SendingEndpoint,
    string ReceivingEndpoint,
    DateTimeOffset OccurredAt,
    MessageProcessingStatus Status,
    string? RelatedToMessageId = null,
    string? SagaInstanceId = null,
    string? FailureId = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? BodyPreview = null);
