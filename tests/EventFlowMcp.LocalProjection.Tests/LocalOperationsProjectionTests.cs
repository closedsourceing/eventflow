using EventFlowMcp.Abstractions.Operations;
using EventFlowMcp.LocalProjection;
using Xunit;

namespace EventFlowMcp.LocalProjection.Tests;

public sealed class LocalOperationsProjectionTests
{
    [Fact]
    public async Task Persists_realistic_trace_saga_and_failure_data_for_a_separate_reader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eventflow-projection-test-{Guid.NewGuid():N}.json");

        try
        {
            var writer = new LocalOperationsProjection(path);
            writer.Reset();
            var occurredAt = DateTimeOffset.UtcNow;
            var headers = new Dictionary<string, string>
            {
                ["NServiceBus.MessageId"] = "message-42",
                ["EventFlow.HandlerType"] = "Orders.SubmitOrderHandler"
            };
            var activity = new MessageActivity(
                "message-42",
                "Orders.SubmitOrder",
                MessageIntent.Command,
                "Orders.Api",
                "Orders.Endpoint",
                occurredAt,
                MessageProcessingStatus.Failed,
                Headers: headers);
            var failure = new FailedMessage(
                "failure-42",
                "message-42",
                "conversation-42",
                "Orders.Endpoint",
                "Orders.SubmitOrder",
                "InvalidOperationException",
                "A deterministic demo failure occurred.",
                occurredAt,
                headers,
                BodyPreview: null);

            writer.RecordActivity(
                "conversation-42",
                activity,
                new Dictionary<string, string> { ["OrderId"] = "order-10042" },
                processingTimeMs: 12.5,
                failure);
            writer.RecordSagaTransition(new SagaTransitionObservation(
                "Orders.OrderSaga:order-10042",
                "Orders.OrderSaga",
                "OrderId",
                "order-10042",
                SagaStatus.Completed,
                "message-42",
                "Orders.SubmitOrder",
                occurredAt,
                "Completed"));

            var reader = new LocalOperationsProjection(path);
            var trace = await reader.GetMessageTraceAsync("order-10042");
            var sagas = await reader.SearchSagasAsync(new SagaSearchRequest(CorrelationValue: "order-10042"));
            var failures = await reader.SearchFailedMessagesAsync(new FailedMessageSearchRequest(MessageType: "SubmitOrder"));
            var health = await reader.GetEndpointHealthAsync();

            Assert.NotNull(trace);
            Assert.Equal("conversation-42", trace.ConversationId);
            Assert.Equal("Orders.SubmitOrderHandler", trace.Activities.Single().Headers!["EventFlow.HandlerType"]);
            Assert.Null(trace.Activities.Single().BodyPreview);
            Assert.Equal(SagaStatus.Completed, Assert.Single(sagas).Status);
            Assert.Equal("failure-42", Assert.Single(failures).Id);
            Assert.Equal("Degraded", Assert.Single(health).Status);
            Assert.Equal(1, health.Single().FailedMessages);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
