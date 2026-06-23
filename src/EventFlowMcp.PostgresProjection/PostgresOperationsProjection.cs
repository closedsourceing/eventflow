using System.Text.Json;
using EventFlowMcp.Abstractions.Operations;
using Npgsql;
using NpgsqlTypes;

namespace EventFlowMcp.PostgresProjection;

/// <summary>
/// Durable operations projection for a shared EventFlow deployment. The writer
/// stores selected metadata only; the MCP server uses the same type exclusively
/// through <see cref="INServiceBusOperationsReader"/>.
/// </summary>
public sealed class PostgresOperationsProjection : INServiceBusOperationsReader, IOperationsProjectionWriter, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource dataSource;

    public PostgresOperationsProjection(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));

        dataSource = NpgsqlDataSource.Create(connectionString);
    }

    /// <summary>
    /// Creates the narrow EventFlow schema. Call this only with the projection
    /// writer identity; MCP reader identities should be granted SELECT only.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            CREATE SCHEMA IF NOT EXISTS eventflow;

            CREATE TABLE IF NOT EXISTS eventflow.activities (
                id uuid PRIMARY KEY,
                occurred_at timestamptz NOT NULL,
                conversation_id text NOT NULL,
                message_id text NOT NULL,
                message_type text NOT NULL,
                intent integer NOT NULL,
                sending_endpoint text NULL,
                receiving_endpoint text NOT NULL,
                status integer NOT NULL,
                related_message_id text NULL,
                saga_instance_id text NULL,
                failure_id text NULL,
                correlation jsonb NOT NULL DEFAULT '{}'::jsonb,
                headers jsonb NOT NULL DEFAULT '{}'::jsonb,
                processing_time_ms double precision NULL
            );

            CREATE INDEX IF NOT EXISTS ix_eventflow_activities_conversation
                ON eventflow.activities (conversation_id, occurred_at);
            CREATE INDEX IF NOT EXISTS ix_eventflow_activities_message
                ON eventflow.activities (message_id);
            CREATE INDEX IF NOT EXISTS ix_eventflow_activities_correlation
                ON eventflow.activities USING gin (correlation);

            CREATE TABLE IF NOT EXISTS eventflow.failures (
                id text PRIMARY KEY,
                message_id text NOT NULL,
                conversation_id text NOT NULL,
                endpoint text NOT NULL,
                message_type text NOT NULL,
                exception_type text NOT NULL,
                exception_message text NOT NULL,
                failed_at timestamptz NOT NULL,
                headers jsonb NOT NULL DEFAULT '{}'::jsonb
            );

            CREATE INDEX IF NOT EXISTS ix_eventflow_failures_search
                ON eventflow.failures (failed_at DESC, endpoint, message_type);

            CREATE TABLE IF NOT EXISTS eventflow.sagas (
                id text PRIMARY KEY,
                saga_type text NOT NULL,
                correlation_property text NOT NULL,
                correlation_value text NOT NULL,
                status integer NOT NULL,
                started_at timestamptz NOT NULL,
                last_updated_at timestamptz NOT NULL,
                state jsonb NOT NULL DEFAULT '{}'::jsonb
            );

            CREATE INDEX IF NOT EXISTS ix_eventflow_sagas_correlation
                ON eventflow.sagas (correlation_value, last_updated_at DESC);

            CREATE TABLE IF NOT EXISTS eventflow.saga_transitions (
                saga_id text NOT NULL REFERENCES eventflow.sagas(id) ON DELETE CASCADE,
                message_id text NOT NULL,
                message_type text NOT NULL,
                occurred_at timestamptz NOT NULL,
                action text NOT NULL,
                notes text NULL,
                PRIMARY KEY (saga_id, message_id, action)
            );
            """;

        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyRetentionAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        if (retentionDays is < 1 or > 3_650)
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "Retention must be between 1 and 3650 days.");

        const string sql = """
            DELETE FROM eventflow.activities WHERE occurred_at < @cutoff;
            DELETE FROM eventflow.failures WHERE failed_at < @cutoff;
            DELETE FROM eventflow.sagas WHERE last_updated_at < @cutoff;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("cutoff", DateTimeOffset.UtcNow.AddDays(-retentionDays));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordActivityAsync(
        string conversationId,
        MessageActivity activity,
        IReadOnlyDictionary<string, string>? correlation = null,
        double? processingTimeMs = null,
        FailedMessage? failure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(activity);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        const string activitySql = """
            INSERT INTO eventflow.activities (
                id, occurred_at, conversation_id, message_id, message_type, intent,
                sending_endpoint, receiving_endpoint, status, related_message_id,
                saga_instance_id, failure_id, correlation, headers, processing_time_ms)
            VALUES (
                @id, @occurredAt, @conversationId, @messageId, @messageType, @intent,
                @sendingEndpoint, @receivingEndpoint, @status, @relatedMessageId,
                @sagaInstanceId, @failureId, @correlation, @headers, @processingTimeMs);
            """;

        await using (var command = new NpgsqlCommand(activitySql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("occurredAt", activity.OccurredAt);
            command.Parameters.AddWithValue("conversationId", conversationId);
            command.Parameters.AddWithValue("messageId", activity.MessageId);
            command.Parameters.AddWithValue("messageType", activity.MessageType);
            command.Parameters.AddWithValue("intent", (int)activity.Intent);
            command.Parameters.AddWithValue("sendingEndpoint", (object?)activity.SendingEndpoint ?? DBNull.Value);
            command.Parameters.AddWithValue("receivingEndpoint", activity.ReceivingEndpoint);
            command.Parameters.AddWithValue("status", (int)activity.Status);
            command.Parameters.AddWithValue("relatedMessageId", (object?)activity.RelatedToMessageId ?? DBNull.Value);
            command.Parameters.AddWithValue("sagaInstanceId", (object?)activity.SagaInstanceId ?? DBNull.Value);
            command.Parameters.AddWithValue("failureId", (object?)activity.FailureId ?? DBNull.Value);
            command.Parameters.Add("correlation", NpgsqlDbType.Jsonb).Value = Serialize(ProjectionDataSanitizer.Sanitize(correlation));
            command.Parameters.Add("headers", NpgsqlDbType.Jsonb).Value = Serialize(ProjectionDataSanitizer.Sanitize(activity.Headers));
            command.Parameters.AddWithValue("processingTimeMs", (object?)processingTimeMs ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (failure is not null)
        {
            const string failureSql = """
                INSERT INTO eventflow.failures (
                    id, message_id, conversation_id, endpoint, message_type, exception_type,
                    exception_message, failed_at, headers)
                VALUES (
                    @id, @messageId, @conversationId, @endpoint, @messageType, @exceptionType,
                    @exceptionMessage, @failedAt, @headers)
                ON CONFLICT (id) DO UPDATE SET
                    exception_type = EXCLUDED.exception_type,
                    exception_message = EXCLUDED.exception_message,
                    failed_at = EXCLUDED.failed_at,
                    headers = EXCLUDED.headers;
                """;

            await using var command = new NpgsqlCommand(failureSql, connection, transaction);
            command.Parameters.AddWithValue("id", failure.Id);
            command.Parameters.AddWithValue("messageId", failure.MessageId);
            command.Parameters.AddWithValue("conversationId", failure.ConversationId);
            command.Parameters.AddWithValue("endpoint", failure.Endpoint);
            command.Parameters.AddWithValue("messageType", failure.MessageType);
            command.Parameters.AddWithValue("exceptionType", failure.ExceptionType);
            command.Parameters.AddWithValue("exceptionMessage", Truncate(failure.ExceptionMessage, 4_096)!);
            command.Parameters.AddWithValue("failedAt", failure.FailedAt);
            command.Parameters.Add("headers", NpgsqlDbType.Jsonb).Value = Serialize(ProjectionDataSanitizer.Sanitize(failure.Headers));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordSagaTransitionAsync(
        SagaTransitionObservation transition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transition);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        const string sagaSql = """
            INSERT INTO eventflow.sagas (
                id, saga_type, correlation_property, correlation_value, status,
                started_at, last_updated_at, state)
            VALUES (
                @id, @sagaType, @correlationProperty, @correlationValue, @status,
                @occurredAt, @occurredAt, @state)
            ON CONFLICT (id) DO UPDATE SET
                saga_type = EXCLUDED.saga_type,
                correlation_property = EXCLUDED.correlation_property,
                correlation_value = EXCLUDED.correlation_value,
                status = EXCLUDED.status,
                last_updated_at = EXCLUDED.last_updated_at,
                state = eventflow.sagas.state || EXCLUDED.state;
            """;
        await using (var command = new NpgsqlCommand(sagaSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", transition.SagaInstanceId);
            command.Parameters.AddWithValue("sagaType", transition.SagaType);
            command.Parameters.AddWithValue("correlationProperty", transition.CorrelationProperty);
            command.Parameters.AddWithValue("correlationValue", transition.CorrelationValue);
            command.Parameters.AddWithValue("status", (int)transition.Status);
            command.Parameters.AddWithValue("occurredAt", transition.OccurredAt);
            command.Parameters.Add("state", NpgsqlDbType.Jsonb).Value = Serialize(ProjectionDataSanitizer.Sanitize(transition.State));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string transitionSql = """
            INSERT INTO eventflow.saga_transitions (saga_id, message_id, message_type, occurred_at, action, notes)
            VALUES (@sagaId, @messageId, @messageType, @occurredAt, @action, @notes)
            ON CONFLICT (saga_id, message_id, action) DO NOTHING;
            """;
        await using (var command = new NpgsqlCommand(transitionSql, connection, transaction))
        {
            command.Parameters.AddWithValue("sagaId", transition.SagaInstanceId);
            command.Parameters.AddWithValue("messageId", transition.MessageId);
            command.Parameters.AddWithValue("messageType", transition.MessageType);
            command.Parameters.AddWithValue("occurredAt", transition.OccurredAt);
            command.Parameters.AddWithValue("action", transition.Action);
            command.Parameters.AddWithValue("notes", (object?)Truncate(transition.Notes, 1_024) ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FailedMessage>> SearchFailedMessagesAsync(
        FailedMessageSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT id, message_id, conversation_id, endpoint, message_type, exception_type,
                   exception_message, failed_at, headers
            FROM eventflow.failures WHERE TRUE
            """;
        var parameters = new List<NpgsqlParameter>();

        AddContainsFilter(ref sql, parameters, "endpoint", request.Endpoint, "endpointFilter");
        AddContainsFilter(ref sql, parameters, "message_type", request.MessageType, "messageTypeFilter");
        AddContainsFilter(ref sql, parameters, "exception_type", request.ExceptionType, "exceptionTypeFilter");
        if (request.Since is not null)
        {
            sql += " AND failed_at >= @since";
            parameters.Add(new NpgsqlParameter("since", request.Since));
        }

        sql += " ORDER BY failed_at DESC LIMIT @limit";
        parameters.Add(new NpgsqlParameter("limit", Math.Clamp(request.MaxResults, 1, 100)));

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var failures = new List<FailedMessage>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            failures.Add(MapFailure(reader));

        return failures;
    }

    public async Task<FailedMessage?> GetFailedMessageByIdAsync(
        string failedMessageId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, message_id, conversation_id, endpoint, message_type, exception_type,
                   exception_message, failed_at, headers
            FROM eventflow.failures WHERE id = @id;
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", failedMessageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapFailure(reader) : null;
    }

    public async Task<IReadOnlyList<EndpointHealth>> GetEndpointHealthAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT a.receiving_endpoint,
                   CASE WHEN COUNT(DISTINCT f.id) > 0 THEN 'Degraded' ELSE 'Healthy' END,
                   MAX(a.occurred_at),
                   COUNT(DISTINCT f.id),
                   AVG(a.processing_time_ms)
            FROM eventflow.activities a
            LEFT JOIN eventflow.failures f ON f.endpoint = a.receiving_endpoint
            GROUP BY a.receiving_endpoint
            ORDER BY a.receiving_endpoint;
            """;
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var endpoints = new List<EndpointHealth>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var failed = reader.GetInt64(3);
            endpoints.Add(new EndpointHealth(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                checked((int)failed),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                failed > 0 ? "One or more projected handler failures exist." : null));
        }

        return endpoints;
    }

    public async Task<MessageTrace?> GetMessageTraceAsync(string identifier, CancellationToken cancellationToken = default)
    {
        const string conversationSql = """
            SELECT conversation_id, correlation
            FROM eventflow.activities
            WHERE conversation_id = @identifier
               OR message_id = @identifier
               OR EXISTS (SELECT 1 FROM jsonb_each_text(correlation) AS item WHERE item.value = @identifier)
            ORDER BY occurred_at DESC
            LIMIT 1;
            """;
        await using var conversationCommand = dataSource.CreateCommand(conversationSql);
        conversationCommand.Parameters.AddWithValue("identifier", identifier);
        await using var conversationReader = await conversationCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await conversationReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var conversationId = conversationReader.GetString(0);
        var correlation = DeserializeDictionary(conversationReader.GetString(1));
        await conversationReader.CloseAsync().ConfigureAwait(false);

        const string activitySql = """
            SELECT message_id, message_type, intent, sending_endpoint, receiving_endpoint,
                   occurred_at, status, related_message_id, saga_instance_id, failure_id, headers
            FROM eventflow.activities
            WHERE conversation_id = @conversationId
            ORDER BY occurred_at;
            """;
        await using var activityCommand = dataSource.CreateCommand(activitySql);
        activityCommand.Parameters.AddWithValue("conversationId", conversationId);
        await using var activityReader = await activityCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var activities = new List<MessageActivity>();
        while (await activityReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            activities.Add(MapActivity(activityReader));

        return new MessageTrace(
            conversationId,
            activities,
            correlation,
            $"{activities.Count} handler invocation(s) read from the PostgreSQL EventFlow projection.");
    }

    public async Task<IReadOnlyList<SagaInstance>> SearchSagasAsync(
        SagaSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT id, saga_type, correlation_property, correlation_value, status,
                   started_at, last_updated_at, state
            FROM eventflow.sagas WHERE TRUE
            """;
        var parameters = new List<NpgsqlParameter>();
        AddContainsFilter(ref sql, parameters, "saga_type", request.SagaType, "sagaTypeFilter");
        AddContainsFilter(ref sql, parameters, "correlation_value", request.CorrelationValue, "correlationValueFilter");
        if (request.Status is not null)
        {
            sql += " AND status = @status";
            parameters.Add(new NpgsqlParameter("status", (int)request.Status));
        }

        sql += " ORDER BY last_updated_at DESC LIMIT @limit";
        parameters.Add(new NpgsqlParameter("limit", Math.Clamp(request.MaxResults, 1, 100)));

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<SagaRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(MapSagaRow(reader));
        await reader.CloseAsync().ConfigureAwait(false);

        var sagas = new List<SagaInstance>();
        foreach (var row in rows)
            sagas.Add(await ReadSagaAsync(row, cancellationToken).ConfigureAwait(false));

        return sagas;
    }

    public async Task<SagaInstance?> GetSagaByIdAsync(string sagaInstanceId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, saga_type, correlation_property, correlation_value, status,
                   started_at, last_updated_at, state
            FROM eventflow.sagas WHERE id = @id;
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", sagaInstanceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var row = MapSagaRow(reader);
        await reader.CloseAsync().ConfigureAwait(false);
        return await ReadSagaAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    private async Task<SagaInstance> ReadSagaAsync(SagaRow row, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT message_id, message_type, occurred_at, action, notes
            FROM eventflow.saga_transitions
            WHERE saga_id = @sagaId
            ORDER BY occurred_at;
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sagaId", row.Id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var transitions = new List<SagaTransition>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            transitions.Add(new SagaTransition(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return new SagaInstance(
            row.Id,
            row.SagaType,
            row.CorrelationProperty,
            row.CorrelationValue,
            (SagaStatus)row.Status,
            row.StartedAt,
            row.LastUpdatedAt,
            transitions,
            DeserializeDictionary(row.State));
    }

    private static MessageActivity MapActivity(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        (MessageIntent)reader.GetInt32(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.GetFieldValue<DateTimeOffset>(5),
        (MessageProcessingStatus)reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        DeserializeDictionary(reader.GetString(10)),
        BodyPreview: null);

    private static FailedMessage MapFailure(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetFieldValue<DateTimeOffset>(7),
        DeserializeDictionary(reader.GetString(8)),
        BodyPreview: null);

    private static SagaRow MapSagaRow(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt32(4),
        reader.GetFieldValue<DateTimeOffset>(5),
        reader.GetFieldValue<DateTimeOffset>(6),
        reader.GetString(7));

    private static void AddContainsFilter(
        ref string sql,
        ICollection<NpgsqlParameter> parameters,
        string column,
        string? value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        sql += $" AND {column} ILIKE @{parameterName}";
        parameters.Add(new NpgsqlParameter(parameterName, $"%{value}%"));
    }

    private static string Serialize(IReadOnlyDictionary<string, string> values)
        => JsonSerializer.Serialize(values, JsonOptions);

    private static IReadOnlyDictionary<string, string> DeserializeDictionary(string json)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
           ?? new Dictionary<string, string>();

    private static string? Truncate(string? value, int maximumLength)
        => value is null || value.Length <= maximumLength ? value : $"{value[..maximumLength]}…";

    private sealed record SagaRow(
        string Id,
        string SagaType,
        string CorrelationProperty,
        string CorrelationValue,
        int Status,
        DateTimeOffset StartedAt,
        DateTimeOffset LastUpdatedAt,
        string State);
}
