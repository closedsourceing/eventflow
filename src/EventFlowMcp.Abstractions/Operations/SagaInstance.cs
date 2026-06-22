namespace EventFlowMcp.Abstractions.Operations;

public enum SagaStatus
{
    Active,
    Completed,
    NotFound
}

public sealed record SagaTransition(
    string MessageId,
    string MessageType,
    DateTimeOffset OccurredAt,
    string Action,
    string? Notes = null);

public sealed record SagaInstance(
    string Id,
    string SagaType,
    string CorrelationProperty,
    string CorrelationValue,
    SagaStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset LastUpdatedAt,
    IReadOnlyList<SagaTransition> Transitions,
    IReadOnlyDictionary<string, string>? State = null);

public sealed record SagaSearchRequest(
    int MaxResults = 10,
    string? SagaType = null,
    string? CorrelationValue = null,
    SagaStatus? Status = null);
