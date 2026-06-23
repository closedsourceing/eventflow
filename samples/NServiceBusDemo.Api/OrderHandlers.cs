using NServiceBus;

namespace NServiceBusDemo.Api;

public sealed class SubmitOrderHandler(DemoOrderStore orders) : IHandleMessages<SubmitOrder>
{
    public async Task Handle(SubmitOrder message, IMessageHandlerContext context)
    {
        orders.MarkSubmitted(message.OrderId);
        await context.Publish(new OrderSubmitted(message.OrderId));
    }
}

public sealed class ApproveOrderHandler(DemoOrderStore orders) : IHandleMessages<ApproveOrder>
{
    public Task Handle(ApproveOrder message, IMessageHandlerContext context)
    {
        orders.MarkApproved(message.OrderId);
        return Task.CompletedTask;
    }
}
