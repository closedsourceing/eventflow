namespace EventFlowMcp.Abstractions.Operations;

/// <summary>
/// Ingests the minimum operational data that EventFlow needs. Implementations
/// must never persist message bodies or sensitive headers by default.
/// </summary>
public interface IOperationsProjectionWriter
{
    Task RecordActivityAsync(
        string conversationId,
        MessageActivity activity,
        IReadOnlyDictionary<string, string>? correlation = null,
        double? processingTimeMs = null,
        FailedMessage? failure = null,
        CancellationToken cancellationToken = default);

    Task RecordSagaTransitionAsync(
        SagaTransitionObservation transition,
        CancellationToken cancellationToken = default);
}

public sealed record SagaTransitionObservation(
    string SagaInstanceId,
    string SagaType,
    string CorrelationProperty,
    string CorrelationValue,
    SagaStatus Status,
    string MessageId,
    string MessageType,
    DateTimeOffset OccurredAt,
    string Action,
    string? Notes = null,
    IReadOnlyDictionary<string, string>? State = null);
