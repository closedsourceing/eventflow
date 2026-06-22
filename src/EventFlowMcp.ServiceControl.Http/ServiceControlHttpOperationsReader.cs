using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventFlowMcp.Abstractions.Operations;
using Microsoft.Extensions.Options;

namespace EventFlowMcp.ServiceControl.Http;

/// <summary>
/// Read-only query adapter for the ServiceControl HTTP routes exercised by ServicePulse.
/// HTTP response translation is isolated in <see cref="ServiceControlResponseMapper"/>,
/// so the EventFlow operations contract does not inherit ServiceControl API details.
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
            query = query.Where(failure => ServiceControlResponseMapper.Contains(failure.ReceivingEndpoint?.Name, request.Endpoint));

        if (!string.IsNullOrWhiteSpace(request.MessageType))
            query = query.Where(failure => ServiceControlResponseMapper.Contains(failure.MessageType, request.MessageType));

        if (!string.IsNullOrWhiteSpace(request.ExceptionType))
            query = query.Where(failure => ServiceControlResponseMapper.Contains(failure.Exception?.ExceptionType, request.ExceptionType));

        if (request.Since is not null)
            query = query.Where(failure => ServiceControlResponseMapper.ToOffset(failure.TimeOfFailure) >= request.Since);

        return query
            .OrderByDescending(failure => failure.TimeOfFailure)
            .Take(Math.Clamp(request.MaxResults, 1, PageSize))
            .Select(failure => ServiceControlResponseMapper.ToFailedMessage(failure))
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

        return ServiceControlResponseMapper.ToFailedMessage(view, details);
    }

    public async Task<IReadOnlyList<EndpointHealth>> GetEndpointHealthAsync(CancellationToken cancellationToken = default)
    {
        var endpoints = await GetRequiredAsync<List<EndpointDto>>("endpoints", cancellationToken);
        var failures = await GetFailureViewsAsync(cancellationToken);

        return endpoints
            .Select(endpoint => ServiceControlResponseMapper.ToEndpointHealth(
                endpoint,
                failures.Count(failure => string.Equals(
                    failure.ReceivingEndpoint?.Name,
                    endpoint.Name,
                    StringComparison.OrdinalIgnoreCase))))
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
            .Select(ServiceControlResponseMapper.ToMessageActivity)
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
        // ServiceControl exposes saga history by saga id, not a global saga-search API.
        // A correlation value therefore resolves a conversation and its invoked sagas.
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
            if (saga is null)
                continue;

            saga = saga with
            {
                CorrelationProperty = "ConversationId",
                CorrelationValue = trace.ConversationId
            };

            if ((string.IsNullOrWhiteSpace(request.SagaType) || ServiceControlResponseMapper.Contains(saga.SagaType, request.SagaType))
                && (request.Status is null || saga.Status == request.Status))
            {
                sagas.Add(saga);
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
        return history is null ? null : ServiceControlResponseMapper.ToSagaInstance(history, sagaInstanceId);
    }

    private async Task<List<MessageViewDto>> TryGetConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync($"conversations/{Escape(conversationId)}?per_page={PageSize}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<MessageViewDto>>(JsonOptions, cancellationToken) ?? [];
    }

    private Task<List<FailedMessageViewDto>> GetFailureViewsAsync(CancellationToken cancellationToken)
        => GetRequiredAsync<List<FailedMessageViewDto>>($"errors?status=unresolved&per_page={PageSize}", cancellationToken);

    private async Task<T> GetRequiredAsync<T>(string relativeUri, CancellationToken cancellationToken)
    {
        var result = await httpClient.GetFromJsonAsync<T>(relativeUri, JsonOptions, cancellationToken);
        return result ?? throw new InvalidOperationException($"ServiceControl returned an empty response for '{relativeUri}'.");
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private int PageSize => Math.Clamp(options.PageSize, 1, 500);
}
