using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventFlowMcp.Abstractions.Operations;
using Microsoft.Extensions.Options;

namespace EventFlowMcp.ServiceControl.Http;

/// <summary>
/// Read-only adapter for the ServiceControl HTTP routes exercised by ServicePulse.
/// Pin and contract-test this adapter against the exact ServiceControl version deployed
/// by a team before enabling it outside development.
/// </summary>
public sealed class ServiceControlHttpOperationsReader : INServiceBusOperationsReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        // ServiceControl serializes its HTTP payloads with snake_case names.
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient httpClient;
    private readonly ServiceControlOptions options;

    public ServiceControlHttpOperationsReader(HttpClient httpClient, IOptions<ServiceControlOptions> options)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
    }

    public async Task<IReadOnlyList<FailedMessage>> SearchFailedMessagesAsync(
        FailedMessageSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var failures = await GetFailureViewsAsync(cancellationToken);

        var query = failures.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(request.Endpoint))
            query = query.Where(failure => Contains(failure.ReceivingEndpoint?.Name, request.Endpoint));

        if (!string.IsNullOrWhiteSpace(request.MessageType))
            query = query.Where(failure => Contains(failure.MessageType, request.MessageType));

        if (!string.IsNullOrWhiteSpace(request.ExceptionType))
            query = query.Where(failure => Contains(failure.Exception?.ExceptionType, request.ExceptionType));

        if (request.Since is not null)
            query = query.Where(failure => ToOffset(failure.TimeOfFailure) >= request.Since);

        return query
            .OrderByDescending(failure => failure.TimeOfFailure)
            .Take(Math.Clamp(request.MaxResults, 1, PageSize))
            .Select(failure => MapFailureView(failure))
            .ToList();
    }

    public async Task<FailedMessage?> GetFailedMessageByIdAsync(
        string failedMessageId,
        CancellationToken cancellationToken = default)
    {
        var viewResponse = await httpClient.GetAsync($"errors/last/{Escape(failedMessageId)}", cancellationToken);
        if (viewResponse.StatusCode == HttpStatusCode.NotFound)
            return null;

        viewResponse.EnsureSuccessStatusCode();
        var view = await viewResponse.Content.ReadFromJsonAsync<FailedMessageViewDto>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("ServiceControl returned an empty failed-message view.");

        ServiceControlFailedMessageDto? details = null;
        var detailsResponse = await httpClient.GetAsync($"errors/{Escape(failedMessageId)}", cancellationToken);
        if (detailsResponse.IsSuccessStatusCode)
            details = await detailsResponse.Content.ReadFromJsonAsync<ServiceControlFailedMessageDto>(JsonOptions, cancellationToken);
        else if (detailsResponse.StatusCode != HttpStatusCode.NotFound)
            detailsResponse.EnsureSuccessStatusCode();

        return MapFailureView(view, details);
    }

    public async Task<IReadOnlyList<EndpointHealth>> GetEndpointHealthAsync(CancellationToken cancellationToken = default)
    {
        var endpoints = await GetRequiredAsync<List<EndpointDto>>("endpoints", cancellationToken);
        var failures = await GetFailureViewsAsync(cancellationToken);

        return endpoints
            .Select(endpoint =>
            {
                var failureCount = failures.Count(failure => string.Equals(
                    failure.ReceivingEndpoint?.Name,
                    endpoint.Name,
                    StringComparison.OrdinalIgnoreCase));
                var heartbeatAt = ToOffset(endpoint.HeartbeatInformation?.LastReportAt);
                var status = endpoint.IsSendingHeartbeats
                    ? "Healthy"
                    : endpoint.MonitorHeartbeat ? "Degraded" : "Unknown";

                return new EndpointHealth(
                    endpoint.Name ?? "unknown",
                    status,
                    heartbeatAt,
                    failureCount,
                    Notes: endpoint.HostDisplayName);
            })
            .OrderBy(endpoint => endpoint.Endpoint, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<MessageTrace?> GetMessageTraceAsync(string identifier, CancellationToken cancellationToken = default)
    {
        var conversation = await TryGetConversationAsync(identifier, cancellationToken);
        if (conversation.Count == 0)
        {
            var searchResults = await GetRequiredAsync<List<MessageViewDto>>(
                $"messages/search/{Escape(identifier)}?per_page={PageSize}",
                cancellationToken);
            var match = searchResults.FirstOrDefault(message =>
                            string.Equals(message.MessageId, identifier, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(message.ConversationId, identifier, StringComparison.OrdinalIgnoreCase))
                        ?? searchResults.FirstOrDefault();

            if (match is null || string.IsNullOrWhiteSpace(match.ConversationId))
                return null;

            conversation = await TryGetConversationAsync(match.ConversationId, cancellationToken);
        }

        if (conversation.Count == 0)
            return null;

        var conversationId = conversation.FirstOrDefault(message => !string.IsNullOrWhiteSpace(message.ConversationId))?.ConversationId
                             ?? identifier;
        var activities = conversation
            .Select(MapActivity)
            .OrderBy(activity => activity.OccurredAt)
            .ToList();
        var correlation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConversationId"] = conversationId
        };

        return new MessageTrace(
            conversationId,
            activities,
            correlation,
            "Trace read from the configured ServiceControl instance. A missing activity can mean auditing, retention, or permissions are incomplete.");
    }

    public async Task<IReadOnlyList<SagaInstance>> SearchSagasAsync(
        SagaSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        // ServiceControl exposes saga history by saga id, not as a global saga search API.
        // A correlation value is therefore required to resolve a conversation and its invoked sagas safely.
        if (string.IsNullOrWhiteSpace(request.CorrelationValue))
            return [];

        var trace = await GetMessageTraceAsync(request.CorrelationValue, cancellationToken);
        if (trace is null)
            return [];

        var sagaIds = trace.Activities
            .Where(activity => !string.IsNullOrWhiteSpace(activity.SagaInstanceId))
            .Select(activity => activity.SagaInstanceId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(request.MaxResults, 1, PageSize));

        var sagas = new List<SagaInstance>();
        foreach (var sagaId in sagaIds)
        {
            var saga = await GetSagaByIdAsync(sagaId, cancellationToken);
            if (saga is not null)
            {
                saga = saga with
                {
                    CorrelationProperty = "ConversationId",
                    CorrelationValue = trace.ConversationId
                };

                if ((string.IsNullOrWhiteSpace(request.SagaType) || Contains(saga.SagaType, request.SagaType))
                    && (request.Status is null || saga.Status == request.Status))
                {
                    sagas.Add(saga);
                }
            }
        }

        return sagas;
    }

    public async Task<SagaInstance?> GetSagaByIdAsync(string sagaInstanceId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(sagaInstanceId, out _))
            return null;

        var response = await httpClient.GetAsync($"sagas/{Escape(sagaInstanceId)}?per_page={PageSize}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        var history = await response.Content.ReadFromJsonAsync<SagaHistoryDto>(JsonOptions, cancellationToken);
        if (history is null)
            return null;

        var changes = history.Changes ?? [];
        var lastChange = changes.OrderBy(change => change.FinishTime ?? change.StartTime).LastOrDefault();
        var transitions = changes
            .SelectMany(MapTransitions)
            .OrderBy(transition => transition.OccurredAt)
            .ToList();

        return new SagaInstance(
            history.SagaId?.ToString() ?? sagaInstanceId,
            history.SagaType ?? "unknown",
            "Unknown",
            "Unknown",
            IsCompleted(lastChange?.Status) ? SagaStatus.Completed : SagaStatus.Active,
            ToOffset(changes.MinBy(change => change.StartTime)?.StartTime),
            ToOffset(lastChange?.FinishTime ?? lastChange?.StartTime),
            transitions,
            ParseState(lastChange?.StateAfterChange));
    }

    private async Task<List<MessageViewDto>> TryGetConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync($"conversations/{Escape(conversationId)}?per_page={PageSize}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<MessageViewDto>>(JsonOptions, cancellationToken) ?? [];
    }

    private async Task<List<FailedMessageViewDto>> GetFailureViewsAsync(CancellationToken cancellationToken)
        => await GetRequiredAsync<List<FailedMessageViewDto>>($"errors?status=unresolved&per_page={PageSize}", cancellationToken);

    private async Task<T> GetRequiredAsync<T>(string relativeUri, CancellationToken cancellationToken)
    {
        var result = await httpClient.GetFromJsonAsync<T>(relativeUri, JsonOptions, cancellationToken);
        return result ?? throw new InvalidOperationException($"ServiceControl returned an empty response for '{relativeUri}'.");
    }

    private static FailedMessage MapFailureView(FailedMessageViewDto view, ServiceControlFailedMessageDto? details = null)
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

    private static MessageActivity MapActivity(MessageViewDto message)
    {
        var invokedSaga = message.InvokedSagas?.FirstOrDefault();
        var originatingSaga = message.OriginatesFromSaga;
        var sagaId = invokedSaga?.SagaId ?? originatingSaga?.SagaId;

        return new MessageActivity(
            message.MessageId ?? message.Id ?? "unknown",
            message.MessageType ?? "unknown",
            MapIntent(message.MessageIntent),
            message.SendingEndpoint?.Name,
            message.ReceivingEndpoint?.Name ?? "unknown",
            ToOffset(message.ProcessedAt ?? message.TimeSent),
            MapStatus(message.Status),
            GetHeader(message.Headers, "NServiceBus.RelatedTo"),
            sagaId?.ToString(),
            null,
            ToStringDictionary(message.Headers));
    }

    private static IEnumerable<SagaTransition> MapTransitions(SagaStateChangeDto change)
    {
        var timestamp = ToOffset(change.FinishTime ?? change.StartTime);
        if (change.InitiatingMessage is not null)
        {
            yield return new SagaTransition(
                change.InitiatingMessage.MessageId ?? "unknown",
                change.InitiatingMessage.MessageType ?? "unknown",
                timestamp,
                ChangeStatus(change.Status),
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

    private static MessageIntent MapIntent(JsonElement? intent)
    {
        var value = JsonValue(intent);
        return value?.ToLowerInvariant() switch
        {
            "publish" => MessageIntent.Event,
            "send" => MessageIntent.Command,
            "reply" => MessageIntent.Reply,
            "timeout" => MessageIntent.Timeout,
            _ => MessageIntent.Unknown
        };
    }

    private static MessageProcessingStatus MapStatus(JsonElement? status)
    {
        var value = JsonValue(status);
        return value?.ToLowerInvariant() switch
        {
            "1" or "failed" or "2" or "repeatedfailure" => MessageProcessingStatus.Failed,
            "3" or "successful" or "4" or "resolvedsuccessfully" => MessageProcessingStatus.Succeeded,
            _ => MessageProcessingStatus.Pending
        };
    }

    private static bool IsCompleted(JsonElement? status)
        => string.Equals(JsonValue(status), "Completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(JsonValue(status), "2", StringComparison.Ordinal);

    private static string ChangeStatus(JsonElement? status)
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

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private int PageSize => Math.Clamp(options.PageSize, 1, 500);

    private static bool Contains(string? source, string fragment)
        => source?.Contains(fragment, StringComparison.OrdinalIgnoreCase) ?? false;

    private static DateTimeOffset ToOffset(DateTime? value)
        => value is null ? DateTimeOffset.MinValue : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

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

    private sealed class FailedMessageViewDto
    {
        public string? Id { get; init; }
        public string? MessageType { get; init; }
        public ExceptionDetailsDto? Exception { get; init; }
        public string? MessageId { get; init; }
        public EndpointDto? ReceivingEndpoint { get; init; }
        public DateTime? TimeOfFailure { get; init; }
    }

    private sealed class ServiceControlFailedMessageDto
    {
        public string? Id { get; init; }
        public List<ProcessingAttemptDto>? ProcessingAttempts { get; init; }
    }

    private sealed class ProcessingAttemptDto
    {
        public FailureDetailsDto? FailureDetails { get; init; }
        public DateTime? AttemptedAt { get; init; }
        public string? MessageId { get; init; }
        public string? Body { get; init; }
        public Dictionary<string, string>? Headers { get; init; }
    }

    private sealed class FailureDetailsDto
    {
        public string? AddressOfFailingEndpoint { get; init; }
        public DateTime? TimeOfFailure { get; init; }
        public ExceptionDetailsDto? Exception { get; init; }
    }

    private sealed class ExceptionDetailsDto
    {
        public string? ExceptionType { get; init; }
        public string? Message { get; init; }
    }

    private sealed class EndpointDto
    {
        public string? Name { get; init; }
        public string? HostDisplayName { get; init; }
        public bool MonitorHeartbeat { get; init; }
        public bool IsSendingHeartbeats { get; init; }
        public HeartbeatInformationDto? HeartbeatInformation { get; init; }
    }

    private sealed class HeartbeatInformationDto
    {
        public DateTime? LastReportAt { get; init; }
    }

    private sealed class MessageViewDto
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

    private sealed class SagaInfoDto
    {
        public Guid? SagaId { get; init; }
    }

    private sealed class SagaHistoryDto
    {
        public Guid? SagaId { get; init; }
        public string? SagaType { get; init; }
        public List<SagaStateChangeDto>? Changes { get; init; }
    }

    private sealed class SagaStateChangeDto
    {
        public DateTime? StartTime { get; init; }
        public DateTime? FinishTime { get; init; }
        public JsonElement? Status { get; init; }
        public string? StateAfterChange { get; init; }
        public InitiatingMessageDto? InitiatingMessage { get; init; }
        public List<ResultingMessageDto>? OutgoingMessages { get; init; }
        public string? Endpoint { get; init; }
    }

    private sealed class InitiatingMessageDto
    {
        public string? MessageId { get; init; }
        public string? MessageType { get; init; }
    }

    private sealed class ResultingMessageDto
    {
        public string? MessageId { get; init; }
        public string? MessageType { get; init; }
        public DateTime? TimeSent { get; init; }
        public string? Destination { get; init; }
    }
}
