using System.Text.Json;
using EventFlowMcp.Abstractions.Operations;

namespace EventFlowMcp.ServiceControl.Http;

/// <summary>
/// Translates ServiceControl's version-specific HTTP representations into the
/// stable operations contract consumed by EventFlow MCP tools.
/// </summary>
internal static class ServiceControlResponseMapper
{
    internal static FailedMessage ToFailedMessage(FailedMessageViewDto view, ServiceControlFailedMessageDto? details = null)
    {
        var lastAttempt = details?.ProcessingAttempts?.OrderBy(attempt => attempt.AttemptedAt).LastOrDefault();
        var headers = lastAttempt?.Headers;
        var conversationId = GetHeader(headers, "NServiceBus.ConversationId")
                             ?? GetHeader(headers, "NServiceBus.CorrelationId")
                             ?? view.MessageId
                             ?? view.Id
                             ?? "unknown";
        var exception = lastAttempt?.FailureDetails?.Exception ?? view.Exception;
        var failedAt = lastAttempt?.FailureDetails?.TimeOfFailure ?? lastAttempt?.AttemptedAt ?? view.TimeOfFailure;

        return new FailedMessage(
            view.Id ?? details?.Id ?? "unknown",
            view.MessageId ?? lastAttempt?.MessageId ?? "unknown",
            conversationId,
            view.ReceivingEndpoint?.Name ?? lastAttempt?.FailureDetails?.AddressOfFailingEndpoint ?? "unknown",
            view.MessageType ?? "unknown",
            exception?.ExceptionType ?? "unknown",
            exception?.Message ?? "No exception message supplied by ServiceControl.",
            ToOffset(failedAt),
            headers,
            lastAttempt?.Body);
    }

    internal static EndpointHealth ToEndpointHealth(EndpointDto endpoint, int failureCount)
    {
        var status = endpoint.IsSendingHeartbeats
            ? "Healthy"
            : endpoint.MonitorHeartbeat ? "Degraded" : "Unknown";

        return new EndpointHealth(
            endpoint.Name ?? "unknown",
            status,
            ToOffset(endpoint.HeartbeatInformation?.LastReportAt),
            failureCount,
            Notes: endpoint.HostDisplayName);
    }

    internal static MessageActivity ToMessageActivity(MessageViewDto message)
    {
        var invokedSaga = message.InvokedSagas?.FirstOrDefault();
        var originatingSaga = message.OriginatesFromSaga;
        var sagaId = invokedSaga?.SagaId ?? originatingSaga?.SagaId;

        return new MessageActivity(
            message.MessageId ?? message.Id ?? "unknown",
            message.MessageType ?? "unknown",
            ToIntent(message.MessageIntent),
            message.SendingEndpoint?.Name,
            message.ReceivingEndpoint?.Name ?? "unknown",
            ToOffset(message.ProcessedAt ?? message.TimeSent),
            ToStatus(message.Status),
            GetHeader(message.Headers, "NServiceBus.RelatedTo"),
            sagaId?.ToString(),
            null,
            ToStringDictionary(message.Headers));
    }

    internal static SagaInstance ToSagaInstance(SagaHistoryDto history, string fallbackId)
    {
        var changes = history.Changes ?? [];
        var lastChange = changes.OrderBy(change => change.FinishTime ?? change.StartTime).LastOrDefault();
        var transitions = changes
            .SelectMany(ToTransitions)
            .OrderBy(transition => transition.OccurredAt)
            .ToList();

        return new SagaInstance(
            history.SagaId?.ToString() ?? fallbackId,
            history.SagaType ?? "unknown",
            "Unknown",
            "Unknown",
            IsCompleted(lastChange?.Status) ? SagaStatus.Completed : SagaStatus.Active,
            ToOffset(changes.MinBy(change => change.StartTime)?.StartTime),
            ToOffset(lastChange?.FinishTime ?? lastChange?.StartTime),
            transitions,
            ParseState(lastChange?.StateAfterChange));
    }

    internal static bool Contains(string? source, string fragment)
        => source?.Contains(fragment, StringComparison.OrdinalIgnoreCase) ?? false;

    internal static DateTimeOffset ToOffset(DateTime? value)
        => value is null ? DateTimeOffset.MinValue : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private static IEnumerable<SagaTransition> ToTransitions(SagaStateChangeDto change)
    {
        var timestamp = ToOffset(change.FinishTime ?? change.StartTime);
        if (change.InitiatingMessage is not null)
        {
            yield return new SagaTransition(
                change.InitiatingMessage.MessageId ?? "unknown",
                change.InitiatingMessage.MessageType ?? "unknown",
                timestamp,
                ToChangeAction(change.Status),
                change.Endpoint);
        }

        foreach (var outgoing in change.OutgoingMessages ?? [])
        {
            yield return new SagaTransition(
                outgoing.MessageId ?? "unknown",
                outgoing.MessageType ?? "unknown",
                ToOffset(outgoing.TimeSent),
                "Sent",
                outgoing.Destination);
        }
    }

    private static MessageIntent ToIntent(JsonElement? intent)
        => JsonValue(intent)?.ToLowerInvariant() switch
        {
            "publish" => MessageIntent.Event,
            "send" => MessageIntent.Command,
            "reply" => MessageIntent.Reply,
            "timeout" => MessageIntent.Timeout,
            _ => MessageIntent.Unknown
        };

    private static MessageProcessingStatus ToStatus(JsonElement? status)
        => JsonValue(status)?.ToLowerInvariant() switch
        {
            "1" or "failed" or "2" or "repeatedfailure" => MessageProcessingStatus.Failed,
            "3" or "successful" or "4" or "resolvedsuccessfully" => MessageProcessingStatus.Succeeded,
            _ => MessageProcessingStatus.Pending
        };

    private static bool IsCompleted(JsonElement? status)
        => string.Equals(JsonValue(status), "Completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(JsonValue(status), "2", StringComparison.Ordinal);

    private static string ToChangeAction(JsonElement? status)
        => JsonValue(status)?.ToLowerInvariant() switch
        {
            "0" or "new" => "Started",
            "1" or "updated" => "Updated",
            "2" or "completed" => "Completed",
            _ => "Updated"
        };

    private static IReadOnlyDictionary<string, string>? ParseState(string? stateAfterChange)
    {
        if (string.IsNullOrWhiteSpace(stateAfterChange))
            return null;

        try
        {
            using var json = JsonDocument.Parse(stateAfterChange);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, string> { ["RawState"] = stateAfterChange };

            return json.RootElement
                .EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string> { ["RawState"] = stateAfterChange };
        }
    }

    private static IReadOnlyDictionary<string, string>? ToStringDictionary(JsonElement? headers)
    {
        if (headers is null || headers.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headers.Value.EnumerateObject())
                result[header.Name] = header.Value.ToString();
        }
        else if (headers.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var header in headers.Value.EnumerateArray())
            {
                if (header.ValueKind != JsonValueKind.Object
                    || !TryGetProperty(header, "key", out var key)
                    || !TryGetProperty(header, "value", out var value))
                {
                    continue;
                }

                result[key.ToString()] = value.ToString();
            }
        }

        return result;
    }

    private static string? GetHeader(Dictionary<string, string>? headers, string key)
        => headers is not null && headers.TryGetValue(key, out var value) ? value : null;

    private static string? GetHeader(JsonElement? headers, string key)
    {
        var values = ToStringDictionary(headers);
        return values is not null && values.TryGetValue(key, out var value) ? value : null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? JsonValue(JsonElement? element)
    {
        if (element is null)
            return null;

        return element.Value.ValueKind switch
        {
            JsonValueKind.String => element.Value.GetString(),
            JsonValueKind.Number => element.Value.GetRawText(),
            _ => element.Value.ToString()
        };
    }
}
