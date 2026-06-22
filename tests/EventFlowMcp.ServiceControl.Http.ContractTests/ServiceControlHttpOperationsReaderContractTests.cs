using System.Net;
using System.Text;
using EventFlowMcp.Abstractions.Operations;
using EventFlowMcp.ServiceControl.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventFlowMcp.ServiceControl.Http.ContractTests;

public sealed class ServiceControlHttpOperationsReaderContractTests
{
    [Fact]
    public async Task SearchFailedMessages_maps_and_filters_the_errors_response()
    {
        var (reader, handler) = CreateReader();

        var failures = await reader.SearchFailedMessagesAsync(new FailedMessageSearchRequest(
            MaxResults: 10,
            Endpoint: "Sales",
            ExceptionType: "Timeout"));

        var failure = Assert.Single(failures);
        Assert.Equal("failed-001", failure.Id);
        Assert.Equal("Company.Contracts.SubmitOrder", failure.MessageType);
        Assert.Equal("System.TimeoutException", failure.ExceptionType);
        Assert.Equal("Sales.Endpoint", failure.Endpoint);
        Assert.Null(failure.BodyPreview);
        Assert.Equal(["/api/errors?status=unresolved&per_page=100"], handler.RequestedPaths);
    }

    [Fact]
    public async Task SearchFailedMessages_obeys_the_since_filter_and_result_limit()
    {
        var (reader, handler) = CreateReader();

        var failures = await reader.SearchFailedMessagesAsync(new FailedMessageSearchRequest(
            MaxResults: 1,
            Since: new DateTimeOffset(2026, 6, 20, 10, 6, 0, TimeSpan.Zero)));

        var failure = Assert.Single(failures);
        Assert.Equal("failed-002", failure.Id);
        Assert.Equal("Billing.Endpoint", failure.Endpoint);
        Assert.Equal(["/api/errors?status=unresolved&per_page=100"], handler.RequestedPaths);
    }

    [Fact]
    public async Task GetFailedMessageById_maps_detail_headers_and_body_from_the_errors_routes()
    {
        var (reader, handler) = CreateReader();

        var failure = await reader.GetFailedMessageByIdAsync("failed-001");

        Assert.NotNull(failure);
        Assert.Equal("order-10042", failure.ConversationId);
        Assert.Equal("Sales.Endpoint", failure.Endpoint);
        Assert.Equal("Order processing timed out after 30 seconds.", failure.ExceptionMessage);
        Assert.Equal("{\"OrderId\":\"order-10042\"}", failure.BodyPreview);
        Assert.Equal("secret-token", failure.Headers!["Authorization"]);
        Assert.Equal(
            ["/api/errors/last/failed-001", "/api/errors/failed-001"],
            handler.RequestedPaths);
    }

    [Fact]
    public async Task GetFailedMessageById_returns_null_when_the_failure_view_is_missing()
    {
        var (reader, handler) = CreateReader();

        var failure = await reader.GetFailedMessageByIdAsync("does-not-exist");

        Assert.Null(failure);
        Assert.Equal(["/api/errors/last/does-not-exist"], handler.RequestedPaths);
    }

    [Fact]
    public async Task GetEndpointHealth_joins_endpoint_heartbeats_with_unresolved_errors()
    {
        var (reader, handler) = CreateReader();

        var endpoints = await reader.GetEndpointHealthAsync();

        Assert.Collection(
            endpoints,
            endpoint =>
            {
                Assert.Equal("Billing.Endpoint", endpoint.Endpoint);
                Assert.Equal("Degraded", endpoint.Status);
                Assert.Equal(1, endpoint.FailedMessages);
            },
            endpoint =>
            {
                Assert.Equal("Sales.Endpoint", endpoint.Endpoint);
                Assert.Equal("Healthy", endpoint.Status);
                Assert.Equal(1, endpoint.FailedMessages);
                Assert.Equal("sales-host-01", endpoint.Notes);
            });
        Assert.Equal(
            ["/api/endpoints", "/api/errors?status=unresolved&per_page=100"],
            handler.RequestedPaths);
    }

    [Fact]
    public async Task GetMessageTrace_resolves_a_message_id_through_search_and_maps_saga_activity()
    {
        var (reader, handler) = CreateReader();

        var trace = await reader.GetMessageTraceAsync("message-001");

        Assert.NotNull(trace);
        Assert.Equal("order-10042", trace.ConversationId);
        Assert.Equal("order-10042", trace.Correlation!["ConversationId"]);
        Assert.Collection(
            trace.Activities,
            activity =>
            {
                Assert.Equal("message-001", activity.MessageId);
                Assert.Equal(MessageIntent.Command, activity.Intent);
                Assert.Equal(MessageProcessingStatus.Succeeded, activity.Status);
                Assert.Equal("c21f3b2c-567c-4a12-b3aa-6eb8170ca5bb", activity.SagaInstanceId);
            },
            activity =>
            {
                Assert.Equal("message-002", activity.MessageId);
                Assert.Equal(MessageIntent.Event, activity.Intent);
                Assert.Equal(MessageProcessingStatus.Succeeded, activity.Status);
                Assert.Equal("message-001", activity.RelatedToMessageId);
            });
        Assert.Equal(
            [
                "/api/conversations/message-001?per_page=100",
                "/api/messages/search/message-001?per_page=100",
                "/api/conversations/order-10042?per_page=100"
            ],
            handler.RequestedPaths);
    }

    [Fact]
    public async Task GetMessageTrace_uses_a_conversation_id_without_an_unnecessary_search()
    {
        var (reader, handler) = CreateReader();

        var trace = await reader.GetMessageTraceAsync("order-10042");

        Assert.NotNull(trace);
        Assert.Equal(2, trace.Activities.Count);
        Assert.Equal(["/api/conversations/order-10042?per_page=100"], handler.RequestedPaths);
    }

    [Fact]
    public async Task GetSagaById_maps_saga_history_and_state_transitions()
    {
        var (reader, handler) = CreateReader();

        var saga = await reader.GetSagaByIdAsync("c21f3b2c-567c-4a12-b3aa-6eb8170ca5bb");

        Assert.NotNull(saga);
        Assert.Equal("Payments.PaymentCollectionSaga", saga.SagaType);
        Assert.Equal(SagaStatus.Completed, saga.Status);
        Assert.Equal("order-10042", saga.State!["OrderId"]);
        Assert.Collection(
            saga.Transitions,
            transition => Assert.Equal("Started", transition.Action),
            transition => Assert.Equal("Sent", transition.Action),
            transition => Assert.Equal("Completed", transition.Action));
        Assert.Equal(
            ["/api/sagas/c21f3b2c-567c-4a12-b3aa-6eb8170ca5bb?per_page=100"],
            handler.RequestedPaths);
    }

    [Fact]
    public async Task SearchSagas_resolves_a_conversation_then_applies_type_and_status_filters()
    {
        var (reader, handler) = CreateReader();

        var sagas = await reader.SearchSagasAsync(new SagaSearchRequest(
            MaxResults: 10,
            SagaType: "collection",
            CorrelationValue: "order-10042",
            Status: SagaStatus.Completed));

        var saga = Assert.Single(sagas);
        Assert.Equal("ConversationId", saga.CorrelationProperty);
        Assert.Equal("order-10042", saga.CorrelationValue);
        Assert.Equal(SagaStatus.Completed, saga.Status);
        Assert.Equal(
            [
                "/api/conversations/order-10042?per_page=100",
                "/api/sagas/c21f3b2c-567c-4a12-b3aa-6eb8170ca5bb?per_page=100"
            ],
            handler.RequestedPaths);
    }

    [Fact]
    public async Task GetSagaById_rejects_an_invalid_identifier_without_an_http_call()
    {
        var (reader, handler) = CreateReader();

        var saga = await reader.GetSagaByIdAsync("not-a-guid");

        Assert.Null(saga);
        Assert.Empty(handler.RequestedPaths);
    }

    private static (ServiceControlHttpOperationsReader Reader, FixtureHttpMessageHandler Handler) CreateReader()
    {
        var handler = new FixtureHttpMessageHandler(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["/api/errors?status=unresolved&per_page=100"] = Fixture("errors.json"),
            ["/api/errors/last/failed-001"] = Fixture("error-last-failed-001.json"),
            ["/api/errors/failed-001"] = Fixture("error-details-failed-001.json"),
            ["/api/endpoints"] = Fixture("endpoints.json"),
            ["/api/messages/search/message-001?per_page=100"] = Fixture("message-search-message-001.json"),
            ["/api/conversations/order-10042?per_page=100"] = Fixture("conversation-order-10042.json"),
            ["/api/sagas/c21f3b2c-567c-4a12-b3aa-6eb8170ca5bb?per_page=100"] = Fixture("saga-c21f3b2c.json")
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://servicecontrol.test/api/") };
        var options = Options.Create(new ServiceControlOptions { ApiBaseUrl = client.BaseAddress.AbsoluteUri, PageSize = 100 });

        return (new ServiceControlHttpOperationsReader(client, options), handler);
    }

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private sealed class FixtureHttpMessageHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        internal List<string> RequestedPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            RequestedPaths.Add(path);

            if (!responses.TryGetValue(path, out var response))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
