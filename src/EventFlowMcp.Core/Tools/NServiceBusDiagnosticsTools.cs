using System.ComponentModel;
using EventFlowMcp.Abstractions.Operations;
using ModelContextProtocol.Server;

namespace EventFlowMcp.Core.Tools;

[McpServerToolType]
public sealed class NServiceBusOperationsTools
{
    [McpServerTool]
    [Description("Finds recent failed NServiceBus messages. Read-only. Message bodies and sensitive headers are omitted unless explicitly requested.")]
    public static async Task<string> GetRecentFailedMessages(
        INServiceBusOperationsReader operations,
        [Description("Maximum number of failed messages to return.")] int maxResults = 10,
        [Description("Optional endpoint name filter, e.g. Billing.Endpoint.")] string? endpoint = null,
        [Description("Optional message type filter, e.g. PaymentCaptured.")] string? messageType = null,
        [Description("Optional exception type filter, e.g. TimeoutException.")] string? exceptionType = null,
        [Description("Include message body previews. Use only when you are authorized to view business data.")] bool includeBodyPreview = false,
        CancellationToken cancellationToken = default)
    {
        var messages = await operations.SearchFailedMessagesAsync(
            new FailedMessageSearchRequest(maxResults, endpoint, messageType, exceptionType),
            cancellationToken);

        if (messages.Count == 0)
            return "No failed messages matched the supplied filters.";

        if (includeBodyPreview)
        {
            messages = await Task.WhenAll(messages.Select(async message =>
                await operations.GetFailedMessageByIdAsync(message.Id, cancellationToken) ?? message));
        }

        return string.Join("\n\n---\n\n", messages.Select(message => FormatFailedMessage(message, includeBodyPreview)));
    }

    [McpServerTool]
    [Description("Gets NServiceBus endpoint health and recent failure counts. Read-only.")]
    public static async Task<string> GetEndpointHealth(
        INServiceBusOperationsReader operations,
        CancellationToken cancellationToken = default)
    {
        var endpoints = await operations.GetEndpointHealthAsync(cancellationToken);

        if (endpoints.Count == 0)
            return "No endpoint health data found.";

        return string.Join("\n", endpoints.Select(x =>
            $"- {x.Endpoint}: {x.Status}, failed messages: {x.FailedMessages}, last heartbeat: {x.LastHeartbeatAt:u}, avg processing: {x.ProcessingTimeMs?.ToString("0") ?? "n/a"} ms, notes: {x.Notes ?? "n/a"}"));
    }

    [McpServerTool]
    [Description("Traces a command, event, timeout, message id, conversation id, or business correlation value across endpoints and sagas. Read-only.")]
    public static async Task<string> TraceBusinessProcess(
        INServiceBusOperationsReader operations,
        [Description("Message id, conversation id, or a business correlation value such as an OrderId.")] string identifier,
        CancellationToken cancellationToken = default)
    {
        var trace = await operations.GetMessageTraceAsync(identifier, cancellationToken);
        if (trace is null)
            return $"No message conversation was found for '{identifier}'. Try a message id, conversation id, or business correlation value.";

        var correlation = trace.Correlation is null || trace.Correlation.Count == 0
            ? "n/a"
            : string.Join(", ", trace.Correlation.Select(pair => $"{pair.Key}={pair.Value}"));

        var activities = string.Join("\n", trace.Activities
            .OrderBy(activity => activity.OccurredAt)
            .Select(FormatActivity));

        return $"""
        Business-process trace

        Conversation: {trace.ConversationId}
        Correlation: {correlation}
        Summary: {trace.Summary ?? "n/a"}

        Timeline:
        {activities}
        """;
    }

    [McpServerTool]
    [Description("Finds NServiceBus saga instances by type, correlation value, or status. Read-only.")]
    public static async Task<string> FindSagaInstances(
        INServiceBusOperationsReader operations,
        [Description("Optional saga type filter, e.g. PaymentCollectionSaga.")] string? sagaType = null,
        [Description("Optional business correlation value, e.g. an OrderId.")] string? correlationValue = null,
        [Description("Optional status: Active or Completed.")] string? status = null,
        [Description("Maximum number of saga instances to return.")] int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseSagaStatus(status, out var sagaStatus))
            return "Status must be Active or Completed.";

        var sagas = await operations.SearchSagasAsync(
            new SagaSearchRequest(maxResults, sagaType, correlationValue, sagaStatus),
            cancellationToken);

        if (sagas.Count == 0)
            return "No saga instances matched the supplied filters.";

        return string.Join("\n\n---\n\n", sagas.Select(FormatSagaSummary));
    }

    [McpServerTool]
    [Description("Shows the timeline and optional state snapshot for one NServiceBus saga instance. Read-only.")]
    public static async Task<string> GetSagaTimeline(
        INServiceBusOperationsReader operations,
        [Description("Saga instance id returned by FindSagaInstances.")] string sagaInstanceId,
        [Description("Include saga state. Use only when you are authorized to view business data.")] bool includeState = false,
        CancellationToken cancellationToken = default)
    {
        var saga = await operations.GetSagaByIdAsync(sagaInstanceId, cancellationToken);
        if (saga is null)
            return $"Saga instance '{sagaInstanceId}' was not found.";

        var transitions = string.Join("\n", saga.Transitions
            .OrderBy(transition => transition.OccurredAt)
            .Select(transition => $"- {transition.OccurredAt:u} [{transition.Action}] {transition.MessageType} ({transition.MessageId}){FormatNotes(transition.Notes)}"));

        var state = includeState
            ? FormatDictionary(saga.State)
            : "Omitted by default. Set includeState=true only when authorized to view saga business data.";

        return $"""
        Saga timeline

        Id: {saga.Id}
        Type: {saga.SagaType}
        Correlation: {saga.CorrelationProperty}={saga.CorrelationValue}
        Status: {saga.Status}
        Started: {saga.StartedAt:u}
        Last updated: {saga.LastUpdatedAt:u}

        Transitions:
        {transitions}

        State:
        {state}
        """;
    }

    [McpServerTool]
    [Description("Explains one failed NServiceBus message, links its available conversation, and suggests safe next steps. Read-only; does not retry or mutate messages.")]
    public static async Task<string> ExplainFailedMessage(
        INServiceBusOperationsReader operations,
        [Description("Failed message id, e.g. failed-001.")] string failedMessageId,
        [Description("Include message body preview. Use only when you are authorized to view business data.")] bool includeBodyPreview = false,
        CancellationToken cancellationToken = default)
    {
        var message = await operations.GetFailedMessageByIdAsync(failedMessageId, cancellationToken);
        if (message is null)
            return $"Failed message '{failedMessageId}' was not found.";

        var trace = await operations.GetMessageTraceAsync(message.ConversationId, cancellationToken);

        var likelyCause = message.ExceptionType switch
        {
            var s when s.Contains("Timeout", StringComparison.OrdinalIgnoreCase) =>
                "Likely transient dependency failure. Check downstream dependency health, timeouts, retry policy and circuit breaker settings.",
            var s when s.Contains("InvalidOperation", StringComparison.OrdinalIgnoreCase) =>
                "Likely business invariant or idempotency problem. Check whether the handler is safe to re-run and whether duplicate delivery is expected.",
            _ => "Cause is not obvious from the exception type alone. Inspect the full exception, headers, handler code and recent deployments."
        };

        return $"""
        Failed message explanation

        {FormatFailedMessage(message, includeBodyPreview)}

        Likely cause:
        {likelyCause}

        Conversation context:
        {FormatTraceContext(trace)}

        Safe next steps:
        1. Verify whether the failure is transient or deterministic.
        2. Check idempotency before retrying.
        3. Check conversation id: {message.ConversationId}.
        4. Check recent deployments for endpoint: {message.Endpoint}
        5. If the root cause is fixed and handler is idempotent, retry from ServiceControl manually.
        """;
    }

    private static string FormatFailedMessage(FailedMessage message, bool includeBodyPreview)
    {
        var headers = FormatHeaders(message.Headers);
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

    private static string FormatActivity(MessageActivity activity)
    {
        var relationship = activity.RelatedToMessageId is null ? string.Empty : $" ← {activity.RelatedToMessageId}";
        var saga = activity.SagaInstanceId is null ? string.Empty : $"; saga={activity.SagaInstanceId}";
        var failure = activity.FailureId is null ? string.Empty : $"; failure={activity.FailureId}";
        return $"- {activity.OccurredAt:u} [{activity.Status}] {activity.Intent} {activity.MessageType}: {activity.SendingEndpoint ?? "external"} → {activity.ReceivingEndpoint} ({activity.MessageId}){relationship}{saga}{failure}";
    }

    private static string FormatSagaSummary(SagaInstance saga)
        => $"""
           Id: {saga.Id}
           Type: {saga.SagaType}
           Correlation: {saga.CorrelationProperty}={saga.CorrelationValue}
           Status: {saga.Status}
           Started: {saga.StartedAt:u}
           Last updated: {saga.LastUpdatedAt:u}
           """;

    private static string FormatTraceContext(MessageTrace? trace)
    {
        if (trace is null)
            return "No corresponding message trace was available from the configured operations source.";

        var failedActivities = trace.Activities.Count(activity => activity.Status == MessageProcessingStatus.Failed);
        var activeSagas = trace.Activities
            .Where(activity => !string.IsNullOrWhiteSpace(activity.SagaInstanceId))
            .Select(activity => activity.SagaInstanceId)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return $"Conversation {trace.ConversationId}: {trace.Activities.Count} activities, {failedActivities} failed; saga instances: {string.Join(", ", activeSagas)}. {trace.Summary}";
    }

    private static string FormatHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return "n/a";

        return string.Join("\n", headers.Select(header => $"  {header.Key}: {(IsSensitive(header.Key) ? "[redacted]" : header.Value)}"));
    }

    private static string FormatDictionary(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0)
            return "n/a";

        return string.Join("\n", values.Select(value => $"  {value.Key}: {(IsSensitive(value.Key) ? "[redacted]" : value.Value)}"));
    }

    private static bool IsSensitive(string key)
        => key.Contains("authorization", StringComparison.OrdinalIgnoreCase)
           || key.Contains("cookie", StringComparison.OrdinalIgnoreCase)
           || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
           || key.Contains("password", StringComparison.OrdinalIgnoreCase)
           || key.Contains("token", StringComparison.OrdinalIgnoreCase)
           || key.Contains("api-key", StringComparison.OrdinalIgnoreCase);

    private static string FormatNotes(string? notes) => string.IsNullOrWhiteSpace(notes) ? string.Empty : $" — {notes}";

    private static bool TryParseSagaStatus(string? value, out SagaStatus? status)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            status = null;
            return true;
        }

        if (Enum.TryParse<SagaStatus>(value, ignoreCase: true, out var parsed)
            && parsed is SagaStatus.Active or SagaStatus.Completed)
        {
            status = parsed;
            return true;
        }

        status = null;
        return false;
    }
}
