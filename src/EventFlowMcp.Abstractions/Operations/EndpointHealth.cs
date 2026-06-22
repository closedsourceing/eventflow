namespace EventFlowMcp.Abstractions.Operations;

public sealed record EndpointHealth(
    string Endpoint,
    string Status,
    DateTimeOffset LastHeartbeatAt,
    int FailedMessages,
    double? ProcessingTimeMs = null,
    string? Notes = null);
