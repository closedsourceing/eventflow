using EventFlowMcp.NServiceBus;

namespace NServiceBusDemo.Api;

/// <summary>Demo bounded-context mapping; production endpoints own their mapping.</summary>
public sealed class OrderMessageCorrelationResolver : IMessageCorrelationResolver
{
    public IReadOnlyDictionary<string, string> Resolve(
        object message,
        IReadOnlyDictionary<string, string> headers)
        => message is IOrderMessage orderMessage
            ? new Dictionary<string, string> { ["OrderId"] = orderMessage.OrderId }
            : new HeaderCorrelationResolver().Resolve(message, headers);
}
