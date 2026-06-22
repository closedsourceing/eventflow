using EventFlowMcp.Abstractions.Operations;

namespace EventFlowMcp.Core.Operations;

/// <summary>
/// A safe local fixture. It lets MCP clients demonstrate tracing and saga tools without
/// connecting to a real ServiceControl instance or exposing production message data.
/// </summary>
public sealed class InMemoryNServiceBusOperationsReader : INServiceBusOperationsReader
{
    private static readonly DateTimeOffset ReferenceTime = DateTimeOffset.UtcNow;

    private static readonly IReadOnlyList<FailedMessage> FailedMessages =
    [
        new(
            Id: "failed-001",
            MessageId: "msg-pay-003",
            ConversationId: "order-10042",
            Endpoint: "Billing.Endpoint",
            MessageType: "Payments.PaymentCaptured",
            ExceptionType: "System.TimeoutException",
            ExceptionMessage: "Timeout while calling external invoice provider.",
            FailedAt: ReferenceTime.AddMinutes(-18),
            Headers: new Dictionary<string, string>
            {
                ["NServiceBus.MessageId"] = "msg-pay-003",
                ["NServiceBus.ConversationId"] = "order-10042",
                ["NServiceBus.CorrelationId"] = "order-10042",
                ["NServiceBus.EnclosedMessageTypes"] = "Payments.PaymentCaptured"
            },
            BodyPreview: "{ \"paymentId\": \"pay-123\", \"orderId\": \"order-10042\" }"),

        new(
            Id: "failed-002",
            MessageId: "msg-order-077-002",
            ConversationId: "order-10077",
            Endpoint: "Shipping.Endpoint",
            MessageType: "Orders.OrderAccepted",
            ExceptionType: "System.InvalidOperationException",
            ExceptionMessage: "Shipment already exists for order order-10077. Handler is not idempotent.",
            FailedAt: ReferenceTime.AddHours(-2),
            Headers: new Dictionary<string, string>
            {
                ["NServiceBus.MessageId"] = "msg-order-077-002",
                ["NServiceBus.ConversationId"] = "order-10077",
                ["NServiceBus.CorrelationId"] = "order-10077",
                ["NServiceBus.EnclosedMessageTypes"] = "Orders.OrderAccepted"
            },
            BodyPreview: "{ \"orderId\": \"order-10077\", \"customerId\": \"customer-88\" }")
    ];

    private static readonly IReadOnlyList<MessageTrace> Traces =
    [
        new(
            ConversationId: "order-10042",
            Summary: "Payment collection is waiting for Billing to process PaymentCaptured.",
            Correlation: new Dictionary<string, string> { ["OrderId"] = "order-10042", ["PaymentId"] = "pay-123" },
            Activities:
            [
                new("msg-order-001", "Orders.PlaceOrder", MessageIntent.Command, "Sales.Api", "Ordering.Endpoint", ReferenceTime.AddMinutes(-24), MessageProcessingStatus.Succeeded),
                new("msg-order-002", "Orders.OrderAccepted", MessageIntent.Event, "Ordering.Endpoint", "Billing.Endpoint", ReferenceTime.AddMinutes(-23), MessageProcessingStatus.Succeeded, "msg-order-001", "saga-payment-10042"),
                new("msg-order-002", "Orders.OrderAccepted", MessageIntent.Event, "Ordering.Endpoint", "Shipping.Endpoint", ReferenceTime.AddMinutes(-23), MessageProcessingStatus.Succeeded, "msg-order-001", "saga-shipping-10042"),
                new("msg-pay-001", "Payments.RequestPayment", MessageIntent.Command, "Billing.Endpoint", "Payments.Endpoint", ReferenceTime.AddMinutes(-22), MessageProcessingStatus.Succeeded, "msg-order-002", "saga-payment-10042"),
                new("msg-pay-003", "Payments.PaymentCaptured", MessageIntent.Event, "Payments.Endpoint", "Billing.Endpoint", ReferenceTime.AddMinutes(-18), MessageProcessingStatus.Failed, "msg-pay-001", "saga-payment-10042", "failed-001")
            ]),
        new(
            ConversationId: "order-10077",
            Summary: "Shipping rejected a duplicate side effect while processing an order event.",
            Correlation: new Dictionary<string, string> { ["OrderId"] = "order-10077" },
            Activities:
            [
                new("msg-order-077-001", "Orders.PlaceOrder", MessageIntent.Command, "Sales.Api", "Ordering.Endpoint", ReferenceTime.AddHours(-2).AddMinutes(-3), MessageProcessingStatus.Succeeded),
                new("msg-order-077-002", "Orders.OrderAccepted", MessageIntent.Event, "Ordering.Endpoint", "Shipping.Endpoint", ReferenceTime.AddHours(-2), MessageProcessingStatus.Failed, "msg-order-077-001", "saga-shipping-10077", "failed-002")
            ])
    ];

    private static readonly IReadOnlyList<SagaInstance> Sagas =
    [
        new(
            Id: "saga-payment-10042",
            SagaType: "Billing.PaymentCollectionSaga",
            CorrelationProperty: "OrderId",
            CorrelationValue: "order-10042",
            Status: SagaStatus.Active,
            StartedAt: ReferenceTime.AddMinutes(-23),
            LastUpdatedAt: ReferenceTime.AddMinutes(-18),
            Transitions:
            [
                new("msg-order-002", "Orders.OrderAccepted", ReferenceTime.AddMinutes(-23), "Started", "Started by OrderAccepted."),
                new("msg-pay-001", "Payments.RequestPayment", ReferenceTime.AddMinutes(-22), "Sent", "Requested payment from Payments.Endpoint."),
                new("msg-pay-003", "Payments.PaymentCaptured", ReferenceTime.AddMinutes(-18), "Failed", "Billing handler failed before the saga could advance.")
            ],
            State: new Dictionary<string, string>
            {
                ["OrderId"] = "order-10042",
                ["PaymentId"] = "pay-123",
                ["InvoiceCreated"] = "false",
                ["Status"] = "AwaitingInvoice"
            }),
        new(
            Id: "saga-shipping-10042",
            SagaType: "Shipping.OrderFulfilmentSaga",
            CorrelationProperty: "OrderId",
            CorrelationValue: "order-10042",
            Status: SagaStatus.Completed,
            StartedAt: ReferenceTime.AddMinutes(-23),
            LastUpdatedAt: ReferenceTime.AddMinutes(-20),
            Transitions:
            [
                new("msg-order-002", "Orders.OrderAccepted", ReferenceTime.AddMinutes(-23), "Started"),
                new("msg-ship-001", "Shipping.CreateShipment", ReferenceTime.AddMinutes(-21), "Sent"),
                new("msg-ship-002", "Shipping.ShipmentCreated", ReferenceTime.AddMinutes(-20), "Completed")
            ],
            State: new Dictionary<string, string> { ["OrderId"] = "order-10042", ["ShipmentId"] = "ship-456" })
    ];

    private static readonly IReadOnlyList<EndpointHealth> EndpointHealth =
    [
        new("Billing.Endpoint", "Degraded", ReferenceTime.AddMinutes(-1), FailedMessages: 4, ProcessingTimeMs: 820, Notes: "Recent timeouts against invoice provider."),
        new("Shipping.Endpoint", "Healthy", ReferenceTime.AddMinutes(-1), FailedMessages: 1, ProcessingTimeMs: 120),
        new("Ordering.Endpoint", "Healthy", ReferenceTime.AddSeconds(-40), FailedMessages: 0, ProcessingTimeMs: 95)
    ];

    public Task<IReadOnlyList<FailedMessage>> SearchFailedMessagesAsync(
        FailedMessageSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<FailedMessage> query = FailedMessages;

        if (!string.IsNullOrWhiteSpace(request.Endpoint))
            query = query.Where(x => x.Endpoint.Contains(request.Endpoint, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(request.MessageType))
            query = query.Where(x => x.MessageType.Contains(request.MessageType, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(request.ExceptionType))
            query = query.Where(x => x.ExceptionType.Contains(request.ExceptionType, StringComparison.OrdinalIgnoreCase));

        if (request.Since is not null)
            query = query.Where(x => x.FailedAt >= request.Since);

        return Task.FromResult<IReadOnlyList<FailedMessage>>(
            query.OrderByDescending(x => x.FailedAt).Take(Math.Clamp(request.MaxResults, 1, 100)).ToList());
    }

    public Task<FailedMessage?> GetFailedMessageByIdAsync(
        string failedMessageId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(FailedMessages.FirstOrDefault(x => string.Equals(x.Id, failedMessageId, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<EndpointHealth>> GetEndpointHealthAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(EndpointHealth);

    public Task<MessageTrace?> GetMessageTraceAsync(string identifier, CancellationToken cancellationToken = default)
    {
        var trace = Traces.FirstOrDefault(trace =>
            string.Equals(trace.ConversationId, identifier, StringComparison.OrdinalIgnoreCase) ||
            trace.Activities.Any(activity => string.Equals(activity.MessageId, identifier, StringComparison.OrdinalIgnoreCase)) ||
            (trace.Correlation?.Values.Any(value => string.Equals(value, identifier, StringComparison.OrdinalIgnoreCase)) ?? false));

        return Task.FromResult(trace);
    }

    public Task<IReadOnlyList<SagaInstance>> SearchSagasAsync(SagaSearchRequest request, CancellationToken cancellationToken = default)
    {
        IEnumerable<SagaInstance> query = Sagas;

        if (!string.IsNullOrWhiteSpace(request.SagaType))
            query = query.Where(x => x.SagaType.Contains(request.SagaType, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(request.CorrelationValue))
            query = query.Where(x => x.CorrelationValue.Contains(request.CorrelationValue, StringComparison.OrdinalIgnoreCase));

        if (request.Status is not null)
            query = query.Where(x => x.Status == request.Status);

        return Task.FromResult<IReadOnlyList<SagaInstance>>(
            query.OrderByDescending(x => x.LastUpdatedAt).Take(Math.Clamp(request.MaxResults, 1, 100)).ToList());
    }

    public Task<SagaInstance?> GetSagaByIdAsync(string sagaInstanceId, CancellationToken cancellationToken = default)
        => Task.FromResult(Sagas.FirstOrDefault(x => string.Equals(x.Id, sagaInstanceId, StringComparison.OrdinalIgnoreCase)));
}
