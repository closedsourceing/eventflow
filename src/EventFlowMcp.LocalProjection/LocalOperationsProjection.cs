using System.Text.Json;
using EventFlowMcp.Abstractions.Operations;

namespace EventFlowMcp.LocalProjection;

/// <summary>
/// A small JSON read model for local development. One application writes handler
/// observations and a separate EventFlow MCP process reads them. It is not a
/// replacement for ServiceControl or a production telemetry store.
/// </summary>
public sealed class LocalOperationsProjection : INServiceBusOperationsReader, IOperationsProjectionWriter
{
    private const int MaximumActivitiesPerConversation = 500;
    private readonly object writeLock = new();
    private readonly string filePath;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public LocalOperationsProjection(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A local operations projection path is required.", nameof(filePath));

        this.filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => filePath;

    /// <summary>Starts a fresh local run without retaining old demo activity.</summary>
    public void Reset()
    {
        lock (writeLock)
        {
            WriteSnapshot(new LocalOperationsSnapshot());
        }
    }

    /// <summary>
    /// Records a completed or failed handler invocation and updates the endpoint
    /// heartbeat. The caller controls which headers and body data are retained.
    /// </summary>
    public void RecordActivity(
        string conversationId,
        MessageActivity activity,
        IReadOnlyDictionary<string, string>? correlation = null,
        double? processingTimeMs = null,
        FailedMessage? failure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(activity);

        lock (writeLock)
        {
            var snapshot = ReadSnapshot();

            if (failure is not null)
            {
                snapshot.FailedMessages.RemoveAll(message => string.Equals(message.Id, failure.Id, StringComparison.OrdinalIgnoreCase));
                snapshot.FailedMessages.Add(failure);
            }

            var traceIndex = snapshot.Traces.FindIndex(trace =>
                string.Equals(trace.ConversationId, conversationId, StringComparison.OrdinalIgnoreCase));
            var existingTrace = traceIndex >= 0 ? snapshot.Traces[traceIndex] : null;
            var activities = existingTrace?.Activities.ToList() ?? [];
            activities.Add(activity);

            if (activities.Count > MaximumActivitiesPerConversation)
                activities = activities.OrderBy(item => item.OccurredAt).TakeLast(MaximumActivitiesPerConversation).ToList();

            var trace = new MessageTrace(
                conversationId,
                activities,
                correlation ?? existingTrace?.Correlation,
                $"{activities.Count} local NServiceBus handler invocation(s) recorded by the EventFlow demo projection.");

            if (traceIndex >= 0)
                snapshot.Traces[traceIndex] = trace;
            else
                snapshot.Traces.Add(trace);

            UpdateEndpointHealth(snapshot, activity, processingTimeMs);
            WriteSnapshot(snapshot);
        }
    }

    /// <summary>Records a saga transition observed after its NServiceBus handler completes.</summary>
    public void RecordSagaTransition(SagaTransitionObservation transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        lock (writeLock)
        {
            var snapshot = ReadSnapshot();
            var index = snapshot.Sagas.FindIndex(saga =>
                string.Equals(saga.Id, transition.SagaInstanceId, StringComparison.OrdinalIgnoreCase));
            var existing = index >= 0 ? snapshot.Sagas[index] : null;
            var transitions = existing?.Transitions.ToList() ?? [];

            if (!transitions.Any(item =>
                    string.Equals(item.MessageId, transition.MessageId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Action, transition.Action, StringComparison.OrdinalIgnoreCase)))
            {
                transitions.Add(new SagaTransition(
                    transition.MessageId,
                    transition.MessageType,
                    transition.OccurredAt,
                    transition.Action,
                    transition.Notes));
            }

            var state = existing?.State?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                        ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (transition.State is not null)
            {
                foreach (var pair in transition.State)
                    state[pair.Key] = pair.Value;
            }

            var saga = new SagaInstance(
                transition.SagaInstanceId,
                transition.SagaType,
                transition.CorrelationProperty,
                transition.CorrelationValue,
                transition.Status,
                existing?.StartedAt ?? transition.OccurredAt,
                transition.OccurredAt,
                transitions,
                state);

            if (index >= 0)
                snapshot.Sagas[index] = saga;
            else
                snapshot.Sagas.Add(saga);

            WriteSnapshot(snapshot);
        }
    }

    public Task<IReadOnlyList<FailedMessage>> SearchFailedMessagesAsync(
        FailedMessageSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<FailedMessage> query = ReadSnapshot().FailedMessages;

        if (!string.IsNullOrWhiteSpace(request.Endpoint))
            query = query.Where(item => item.Endpoint.Contains(request.Endpoint, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(request.MessageType))
            query = query.Where(item => item.MessageType.Contains(request.MessageType, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(request.ExceptionType))
            query = query.Where(item => item.ExceptionType.Contains(request.ExceptionType, StringComparison.OrdinalIgnoreCase));
        if (request.Since is not null)
            query = query.Where(item => item.FailedAt >= request.Since);

        return Task.FromResult<IReadOnlyList<FailedMessage>>(
            query.OrderByDescending(item => item.FailedAt).Take(Math.Clamp(request.MaxResults, 1, 100)).ToList());
    }

    public Task<FailedMessage?> GetFailedMessageByIdAsync(
        string failedMessageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadSnapshot().FailedMessages.FirstOrDefault(item =>
            string.Equals(item.Id, failedMessageId, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<IReadOnlyList<EndpointHealth>> GetEndpointHealthAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<EndpointHealth>>(
            ReadSnapshot().EndpointHealth.OrderBy(item => item.Endpoint, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public Task<MessageTrace?> GetMessageTraceAsync(string identifier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var trace = ReadSnapshot().Traces.FirstOrDefault(item =>
            string.Equals(item.ConversationId, identifier, StringComparison.OrdinalIgnoreCase)
            || item.Activities.Any(activity => string.Equals(activity.MessageId, identifier, StringComparison.OrdinalIgnoreCase))
            || (item.Correlation?.Values.Any(value => string.Equals(value, identifier, StringComparison.OrdinalIgnoreCase)) ?? false));

        return Task.FromResult(trace);
    }

    public Task<IReadOnlyList<SagaInstance>> SearchSagasAsync(
        SagaSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<SagaInstance> query = ReadSnapshot().Sagas;

        if (!string.IsNullOrWhiteSpace(request.SagaType))
            query = query.Where(item => item.SagaType.Contains(request.SagaType, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(request.CorrelationValue))
            query = query.Where(item => item.CorrelationValue.Contains(request.CorrelationValue, StringComparison.OrdinalIgnoreCase));
        if (request.Status is not null)
            query = query.Where(item => item.Status == request.Status);

        return Task.FromResult<IReadOnlyList<SagaInstance>>(
            query.OrderByDescending(item => item.LastUpdatedAt).Take(Math.Clamp(request.MaxResults, 1, 100)).ToList());
    }

    public Task<SagaInstance?> GetSagaByIdAsync(string sagaInstanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadSnapshot().Sagas.FirstOrDefault(item =>
            string.Equals(item.Id, sagaInstanceId, StringComparison.OrdinalIgnoreCase)));
    }

    public Task RecordActivityAsync(
        string conversationId,
        MessageActivity activity,
        IReadOnlyDictionary<string, string>? correlation = null,
        double? processingTimeMs = null,
        FailedMessage? failure = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecordActivity(conversationId, activity, correlation, processingTimeMs, failure);
        return Task.CompletedTask;
    }

    public Task RecordSagaTransitionAsync(
        SagaTransitionObservation transition,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecordSagaTransition(transition);
        return Task.CompletedTask;
    }

    private LocalOperationsSnapshot ReadSnapshot()
    {
        try
        {
            if (!File.Exists(filePath))
                return new LocalOperationsSnapshot();

            using var stream = File.OpenRead(filePath);
            return JsonSerializer.Deserialize<LocalOperationsSnapshot>(stream, JsonOptions) ?? new LocalOperationsSnapshot();
        }
        catch (IOException)
        {
            // A writer uses atomic replacement, but a reader can still race a file-system operation.
            return new LocalOperationsSnapshot();
        }
        catch (JsonException)
        {
            // A malformed local file must not bring down the read-only MCP server.
            return new LocalOperationsSnapshot();
        }
    }

    private void WriteSnapshot(LocalOperationsSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void UpdateEndpointHealth(
        LocalOperationsSnapshot snapshot,
        MessageActivity activity,
        double? processingTimeMs)
    {
        var failedMessages = snapshot.FailedMessages.Count(message =>
            string.Equals(message.Endpoint, activity.ReceivingEndpoint, StringComparison.OrdinalIgnoreCase));
        var status = failedMessages > 0 ? "Degraded" : "Healthy";
        var notes = failedMessages > 0 ? "One or more local demo handler invocations failed." : null;
        var health = new EndpointHealth(
            activity.ReceivingEndpoint,
            status,
            activity.OccurredAt,
            failedMessages,
            processingTimeMs,
            notes);
        var index = snapshot.EndpointHealth.FindIndex(item =>
            string.Equals(item.Endpoint, activity.ReceivingEndpoint, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
            snapshot.EndpointHealth[index] = health;
        else
            snapshot.EndpointHealth.Add(health);
    }

    private sealed class LocalOperationsSnapshot
    {
        public List<FailedMessage> FailedMessages { get; init; } = [];
        public List<EndpointHealth> EndpointHealth { get; init; } = [];
        public List<MessageTrace> Traces { get; init; } = [];
        public List<SagaInstance> Sagas { get; init; } = [];
    }
}
