using NServiceBus;

namespace NServiceBusDemo.Api;

public interface IOrderMessage
{
    string OrderId { get; }
}

public sealed record SubmitOrder(string OrderId, string Customer, decimal Total) : ICommand, IOrderMessage;

public sealed record ApproveOrder(string OrderId) : ICommand, IOrderMessage;

public sealed record OrderSubmitted(string OrderId) : IEvent, IOrderMessage;
