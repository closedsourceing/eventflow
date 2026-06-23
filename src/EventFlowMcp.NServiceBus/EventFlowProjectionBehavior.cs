using System.Diagnostics;
using EventFlowMcp.Abstractions.Operations;
using NServiceBus;
using NServiceBus.Pipeline;

namespace EventFlowMcp.NServiceBus;

/// <summary>
/// NServiceBus handler-pipeline behavior that writes a narrow operational record
/// to EventFlow. It intentionally excludes message bodies and arbitrary headers.
/// </summary>
public sealed class EventFlowProjectionBehavior(
    IOperationsProjectionWriter projection,
    string receivingEndpoint,
    IMessageCorrelationResolver? correlationResolver = null)
    : Behavior<IInvokeHandlerContext>
{
    private readonly IMessageCorrelationResolver correlationResolver = correlationResolver ?? new HeaderCorrelationResolver();

    public override async Task Invoke(IInvokeHandlerContext context, Func<Task> next)
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var message = context.MessageBeingHandled;
        var messageType = message.GetType();
        var handlerType = context.MessageHandler.HandlerType;
        var messageId = Header(context.Headers, Headers.MessageId) ?? Guid.NewGuid().ToString("N");
        var conversationId = Header(context.Headers, Headers.ConversationId) ?? messageId;
        var correlation = correlationResolver.Resolve(message, context.Headers);
        var sagaInstanceId = IsSaga(handlerType) ? SagaInstanceId(handlerType, correlation, conversationId) : null;

        try
        {
            await next().ConfigureAwait(false);
            stopwatch.Stop();
            await projection.RecordActivityAsync(
                conversationId,
                CreateActivity(context, message, messageType, handlerType, messageId, occurredAt,
                    MessageProcessingStatus.Succeeded, sagaInstanceId),
                correlation,
                stopwatch.Elapsed.TotalMilliseconds).ConfigureAwait(false);

            if (sagaInstanceId is not null)
                await RecordSagaTransitionAsync(context, handlerType, messageType, messageId, occurredAt,
                    sagaInstanceId, correlation, conversationId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            var failureId = $"eventflow-{messageId}-{handlerType.Name}";
            var activity = CreateActivity(context, message, messageType, handlerType, messageId, occurredAt,
                MessageProcessingStatus.Failed, sagaInstanceId, failureId);
            var failure = new FailedMessage(
                failureId,
                messageId,
                conversationId,
                receivingEndpoint,
                MessageTypeName(messageType),
                exception.GetType().FullName ?? exception.GetType().Name,
                Truncate(exception.Message, 4_096),
                occurredAt,
                activity.Headers,
                BodyPreview: null);

            await projection.RecordActivityAsync(
                conversationId,
                activity,
                correlation,
                stopwatch.Elapsed.TotalMilliseconds,
                failure).ConfigureAwait(false);

            if (sagaInstanceId is not null)
            {
                await projection.RecordSagaTransitionAsync(new SagaTransitionObservation(
                    sagaInstanceId,
                    handlerType.FullName ?? handlerType.Name,
                    CorrelationProperty(correlation),
                    CorrelationValue(correlation, conversationId),
                    SagaStatus.Active,
                    messageId,
                    MessageTypeName(messageType),
                    occurredAt,
                    "Failed",
                    $"{handlerType.Name} threw {exception.GetType().Name}.")).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task RecordSagaTransitionAsync(
        IInvokeHandlerContext context,
        Type handlerType,
        Type messageType,
        string messageId,
        DateTimeOffset occurredAt,
        string sagaInstanceId,
        IReadOnlyDictionary<string, string> correlation,
        string conversationId)
    {
        var completed = context.MessageHandler.Instance is Saga saga && saga.Completed;
        var action = completed ? "Completed" : StartsSaga(handlerType, messageType) ? "Started" : "Handled";
        var status = completed ? SagaStatus.Completed : SagaStatus.Active;
        var correlationProperty = CorrelationProperty(correlation);
        var correlationValue = CorrelationValue(correlation, conversationId);

        await projection.RecordSagaTransitionAsync(new SagaTransitionObservation(
            sagaInstanceId,
            handlerType.FullName ?? handlerType.Name,
            correlationProperty,
            correlationValue,
            status,
            messageId,
            MessageTypeName(messageType),
            occurredAt,
            action,
            $"{handlerType.Name} {action.ToLowerInvariant()} during NServiceBus processing.",
            new Dictionary<string, string>
            {
                [correlationProperty] = correlationValue,
                ["LastHandler"] = handlerType.Name,
                ["ObservedStatus"] = status.ToString()
            })).ConfigureAwait(false);
    }

    private MessageActivity CreateActivity(
        IInvokeHandlerContext context,
        object message,
        Type messageType,
        Type handlerType,
        string messageId,
        DateTimeOffset occurredAt,
        MessageProcessingStatus status,
        string? sagaInstanceId,
        string? failureId = null)
    {
        return new MessageActivity(
            messageId,
            MessageTypeName(messageType),
            Intent(message),
            Header(context.Headers, Headers.OriginatingEndpoint),
            receivingEndpoint,
            occurredAt,
            status,
            Header(context.Headers, Headers.RelatedTo),
            sagaInstanceId,
            failureId,
            SelectedHeaders(context.Headers, handlerType),
            BodyPreview: null);
    }

    private static IReadOnlyDictionary<string, string> SelectedHeaders(
        IReadOnlyDictionary<string, string> source,
        Type handlerType)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EventFlow.HandlerType"] = handlerType.FullName ?? handlerType.Name
        };
        foreach (var name in new[] { Headers.MessageId, Headers.ConversationId, Headers.CorrelationId, Headers.OriginatingEndpoint })
        {
            if (source.TryGetValue(name, out var value))
                headers[name] = value;
        }

        return headers;
    }

    private static string? Header(IReadOnlyDictionary<string, string> headers, string name)
        => headers.TryGetValue(name, out var value) ? value : null;

    private static EventFlowMcp.Abstractions.Operations.MessageIntent Intent(object message) => message switch
    {
        ICommand => EventFlowMcp.Abstractions.Operations.MessageIntent.Command,
        IEvent => EventFlowMcp.Abstractions.Operations.MessageIntent.Event,
        _ => EventFlowMcp.Abstractions.Operations.MessageIntent.Unknown
    };

    private static bool IsSaga(Type handlerType) => typeof(Saga).IsAssignableFrom(handlerType);

    private static bool StartsSaga(Type handlerType, Type messageType)
        => handlerType.GetInterfaces().Any(@interface =>
            @interface.IsGenericType
            && @interface.GetGenericTypeDefinition() == typeof(IAmStartedByMessages<>)
            && @interface.GetGenericArguments()[0].IsAssignableFrom(messageType));

    private static string SagaInstanceId(Type handlerType, IReadOnlyDictionary<string, string> correlation, string conversationId)
        => $"{handlerType.FullName ?? handlerType.Name}:{CorrelationValue(correlation, conversationId)}";

    private static string CorrelationProperty(IReadOnlyDictionary<string, string> correlation)
        => correlation.Keys.FirstOrDefault() ?? "NServiceBus.ConversationId";

    private static string CorrelationValue(IReadOnlyDictionary<string, string> correlation, string fallback)
        => correlation.Values.FirstOrDefault() ?? fallback;

    private static string MessageTypeName(Type messageType) => messageType.FullName ?? messageType.Name;

    private static string Truncate(string value, int maximumLength)
        => value.Length <= maximumLength ? value : $"{value[..maximumLength]}…";
}
