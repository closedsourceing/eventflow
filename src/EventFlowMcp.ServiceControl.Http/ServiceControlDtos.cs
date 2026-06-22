using System.Text.Json;

namespace EventFlowMcp.ServiceControl.Http;

// These types intentionally model only the version-pinned ServiceControl routes
// consumed by this adapter. They are not part of EventFlow's public contract.
internal sealed class FailedMessageViewDto
{
    public string? Id { get; init; }
    public string? MessageType { get; init; }
    public ExceptionDetailsDto? Exception { get; init; }
    public string? MessageId { get; init; }
    public EndpointDto? ReceivingEndpoint { get; init; }
    public DateTime? TimeOfFailure { get; init; }
}

internal sealed class ServiceControlFailedMessageDto
{
    public string? Id { get; init; }
    public List<ProcessingAttemptDto>? ProcessingAttempts { get; init; }
}

internal sealed class ProcessingAttemptDto
{
    public FailureDetailsDto? FailureDetails { get; init; }
    public DateTime? AttemptedAt { get; init; }
    public string? MessageId { get; init; }
    public string? Body { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
}

internal sealed class FailureDetailsDto
{
    public string? AddressOfFailingEndpoint { get; init; }
    public DateTime? TimeOfFailure { get; init; }
    public ExceptionDetailsDto? Exception { get; init; }
}

internal sealed class ExceptionDetailsDto
{
    public string? ExceptionType { get; init; }
    public string? Message { get; init; }
}

internal sealed class EndpointDto
{
    public string? Name { get; init; }
    public string? HostDisplayName { get; init; }
    public bool MonitorHeartbeat { get; init; }
    public bool IsSendingHeartbeats { get; init; }
    public HeartbeatInformationDto? HeartbeatInformation { get; init; }
}

internal sealed class HeartbeatInformationDto
{
    public DateTime? LastReportAt { get; init; }
}

internal sealed class MessageViewDto
{
    public string? Id { get; init; }
    public string? MessageId { get; init; }
    public string? MessageType { get; init; }
    public EndpointDto? SendingEndpoint { get; init; }
    public EndpointDto? ReceivingEndpoint { get; init; }
    public DateTime? TimeSent { get; init; }
    public DateTime? ProcessedAt { get; init; }
    public string? ConversationId { get; init; }
    public JsonElement? Headers { get; init; }
    public JsonElement? Status { get; init; }
    public JsonElement? MessageIntent { get; init; }
    public List<SagaInfoDto>? InvokedSagas { get; init; }
    public SagaInfoDto? OriginatesFromSaga { get; init; }
}

internal sealed class SagaInfoDto
{
    public Guid? SagaId { get; init; }
}

internal sealed class SagaHistoryDto
{
    public Guid? SagaId { get; init; }
    public string? SagaType { get; init; }
    public List<SagaStateChangeDto>? Changes { get; init; }
}

internal sealed class SagaStateChangeDto
{
    public DateTime? StartTime { get; init; }
    public DateTime? FinishTime { get; init; }
    public JsonElement? Status { get; init; }
    public string? StateAfterChange { get; init; }
    public InitiatingMessageDto? InitiatingMessage { get; init; }
    public List<ResultingMessageDto>? OutgoingMessages { get; init; }
    public string? Endpoint { get; init; }
}

internal sealed class InitiatingMessageDto
{
    public string? MessageId { get; init; }
    public string? MessageType { get; init; }
}

internal sealed class ResultingMessageDto
{
    public string? MessageId { get; init; }
    public string? MessageType { get; init; }
    public DateTime? TimeSent { get; init; }
    public string? Destination { get; init; }
}
