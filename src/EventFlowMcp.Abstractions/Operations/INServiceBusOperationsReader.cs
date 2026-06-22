namespace EventFlowMcp.Abstractions.Operations;

/// <summary>
/// Read-only operational view of an NServiceBus system. Implementations may use
/// ServiceControl exports, forwarded audit/error queues, or a team-owned projection.
/// </summary>
public interface INServiceBusOperationsReader
{
    Task<IReadOnlyList<FailedMessage>> SearchFailedMessagesAsync(
        FailedMessageSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<FailedMessage?> GetFailedMessageByIdAsync(
        string failedMessageId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EndpointHealth>> GetEndpointHealthAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a message conversation using a message id, conversation id, or business correlation value.
    /// </summary>
    Task<MessageTrace?> GetMessageTraceAsync(
        string identifier,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SagaInstance>> SearchSagasAsync(
        SagaSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<SagaInstance?> GetSagaByIdAsync(
        string sagaInstanceId,
        CancellationToken cancellationToken = default);
}
