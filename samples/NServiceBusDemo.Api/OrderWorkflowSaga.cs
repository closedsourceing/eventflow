using NServiceBus;

namespace NServiceBusDemo.Api;

public sealed class OrderWorkflowSaga : Saga<OrderWorkflowSagaData>,
    IAmStartedByMessages<SubmitOrder>,
    IHandleMessages<ApproveOrder>
{
    protected override void ConfigureHowToFindSaga(SagaPropertyMapper<OrderWorkflowSagaData> mapper)
    {
        mapper.MapSaga(saga => saga.OrderId)
            .ToMessage<SubmitOrder>(message => message.OrderId)
            .ToMessage<ApproveOrder>(message => message.OrderId);
    }

    public Task Handle(SubmitOrder message, IMessageHandlerContext context)
    {
        Data.OrderId = message.OrderId;
        Data.Status = "AwaitingApproval";
        return Task.CompletedTask;
    }

    public Task Handle(ApproveOrder message, IMessageHandlerContext context)
    {
        Data.Status = "Completed";
        MarkAsComplete();
        return Task.CompletedTask;
    }
}

public sealed class OrderWorkflowSagaData : ContainSagaData
{
    public string OrderId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
