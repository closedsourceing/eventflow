using NServiceBus;

namespace EventFlowMcp.NServiceBus;

/// <summary>
/// Maps a message to safe business correlation values. Implement this per bounded
/// context; do not return customer data, payloads, or credentials.
/// </summary>
public interface IMessageCorrelationResolver
{
    IReadOnlyDictionary<string, string> Resolve(
        object message,
        IReadOnlyDictionary<string, string> headers);
}

/// <summary>Safe fallback when an endpoint has not supplied business correlation.</summary>
public sealed class HeaderCorrelationResolver : IMessageCorrelationResolver
{
    public IReadOnlyDictionary<string, string> Resolve(
        object message,
        IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetValue(Headers.CorrelationId, out var correlationId)
            && !string.IsNullOrWhiteSpace(correlationId))
        {
            return new Dictionary<string, string> { ["NServiceBus.CorrelationId"] = correlationId };
        }

        return new Dictionary<string, string>();
    }
}
