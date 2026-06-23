using NServiceBus.Testing;
using NServiceBusDemo.Api;
using Xunit;

namespace NServiceBusDemo.Api.Tests;

public sealed class OrderHandlerTests
{
    [Fact]
    public async Task SubmitOrder_marks_the_hardcoded_order_submitted_and_publishes_an_event()
    {
        var orders = new DemoOrderStore();
        var handler = new SubmitOrderHandler(orders);
        var context = new TestableMessageHandlerContext();

        await handler.Handle(new SubmitOrder(DemoOrderStore.DemoOrderId, "Ada Lovelace", 149.95m), context);

        Assert.True(orders.TryGet(DemoOrderStore.DemoOrderId, out var order));
        Assert.Equal("Submitted", order.Status);
        var published = Assert.Single(context.PublishedMessages);
        Assert.Equal(DemoOrderStore.DemoOrderId, Assert.IsType<OrderSubmitted>(published.Message).OrderId);
    }

    [Fact]
    public async Task ApproveOrder_marks_the_hardcoded_order_approved()
    {
        var orders = new DemoOrderStore();
        var handler = new ApproveOrderHandler(orders);
        var context = new TestableMessageHandlerContext();

        await handler.Handle(new ApproveOrder(DemoOrderStore.DemoOrderId), context);

        Assert.True(orders.TryGet(DemoOrderStore.DemoOrderId, out var order));
        Assert.Equal("Approved", order.Status);
        Assert.Empty(context.PublishedMessages);
    }
}
