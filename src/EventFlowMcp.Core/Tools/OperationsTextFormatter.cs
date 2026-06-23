using EventFlowMcp.Abstractions.Operations;

namespace EventFlowMcp.Core.Tools;

/// <summary>
/// Converts read-only operations data to safe, human-readable MCP responses.
/// Keeping presentation and redaction here prevents transport tools from
/// accidentally exposing sensitive headers or state when new operations are added.
/// </summary>
internal static class OperationsTextFormatter
{
    internal static string FailedMessage(FailedMessage message, bool includeBodyPreview)
    {
        var headers = Headers(message.Headers);
        var bodyPreview = includeBodyPreview
            ? message.BodyPreview ?? "n/a"
            : "Omitted by default. Set includeBodyPreview=true only when authorized to view business data.";

        return $"""
        Id: {message.Id}
        Message id: {message.MessageId}
        Conversation id: {message.ConversationId}
        Endpoint: {message.Endpoint}
        Message type: {message.MessageType}
        Exception: {message.ExceptionType}
        Message: {message.ExceptionMessage}
        Failed at: {message.FailedAt:u}
        Headers:
        {headers}
        Body preview:
        {bodyPreview}
        """;
    }

    internal static string EndpointHealth(EndpointHealth endpoint)
        => $"- {endpoint.Endpoint}: {endpoint.Status}, failed messages: {endpoint.FailedMessages}, last heartbeat: {endpoint.LastHeartbeatAt:u}, avg processing: {endpoint.ProcessingTimeMs?.ToString("0") ?? "n/a"} ms, notes: {endpoint.Notes ?? "n/a"}";

    internal static string Activity(MessageActivity activity)
    {
        var relationship = activity.RelatedToMessageId is null ? string.Empty : $" ← {activity.RelatedToMessageId}";
        var saga = activity.SagaInstanceId is null ? string.Empty : $"; saga={activity.SagaInstanceId}";
        var failure = activity.FailureId is null ? string.Empty : $"; failure={activity.FailureId}";
        var handler = activity.Headers is not null
                      && activity.Headers.TryGetValue("EventFlow.HandlerType", out var handlerType)
            ? $"; handler={handlerType}"
            : string.Empty;
        return $"- {activity.OccurredAt:u} [{activity.Status}] {activity.Intent} {activity.MessageType}: {activity.SendingEndpoint ?? "external"} → {activity.ReceivingEndpoint} ({activity.MessageId}){relationship}{saga}{failure}{handler}";
    }

    internal static string SagaSummary(SagaInstance saga)
        => $"""
           Id: {saga.Id}
           Type: {saga.SagaType}
           Correlation: {saga.CorrelationProperty}={saga.CorrelationValue}
           Status: {saga.Status}
           Started: {saga.StartedAt:u}
           Last updated: {saga.LastUpdatedAt:u}
           """;

    internal static string SagaTransition(SagaTransition transition)
        => $"- {transition.OccurredAt:u} [{transition.Action}] {transition.MessageType} ({transition.MessageId}){Notes(transition.Notes)}";

    internal static string TraceContext(MessageTrace? trace)
    {
        if (trace is null)
            return "No corresponding message trace was available from the configured operations source.";

        var failedActivities = trace.Activities.Count(activity => activity.Status == MessageProcessingStatus.Failed);
        var sagaIds = trace.Activities
            .Where(activity => !string.IsNullOrWhiteSpace(activity.SagaInstanceId))
            .Select(activity => activity.SagaInstanceId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sagas = sagaIds.Count == 0 ? "none" : string.Join(", ", sagaIds);

        return $"Conversation {trace.ConversationId}: {trace.Activities.Count} activities, {failedActivities} failed; saga instances: {sagas}. {trace.Summary}";
    }

    internal static string Dictionary(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0)
            return "n/a";

        return string.Join("\n", values.Select(value => $"  {value.Key}: {(IsSensitive(value.Key) ? "[redacted]" : value.Value)}"));
    }

    private static string Headers(IReadOnlyDictionary<string, string>? headers) => Dictionary(headers);

    private static string Notes(string? notes) => string.IsNullOrWhiteSpace(notes) ? string.Empty : $" — {notes}";

    private static bool IsSensitive(string key)
        => key.Contains("authorization", StringComparison.OrdinalIgnoreCase)
           || key.Contains("cookie", StringComparison.OrdinalIgnoreCase)
           || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
           || key.Contains("password", StringComparison.OrdinalIgnoreCase)
           || key.Contains("token", StringComparison.OrdinalIgnoreCase)
           || key.Contains("api-key", StringComparison.OrdinalIgnoreCase);
}
