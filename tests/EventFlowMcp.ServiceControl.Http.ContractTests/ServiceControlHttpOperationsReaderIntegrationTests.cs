using EventFlowMcp.Abstractions.Operations;
using EventFlowMcp.ServiceControl.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventFlowMcp.ServiceControl.Http.ContractTests;

/// <summary>
/// Exercises the adapter through an actual local HTTP listener rather than an
/// in-memory message handler. The fixture owns the listener lifetime and stops it
/// when the test class completes.
/// </summary>
public sealed class ServiceControlHttpOperationsReaderIntegrationTests(LocalServiceControlApi testApi)
    : IClassFixture<LocalServiceControlApi>
{
    [Fact]
    public async Task Failed_message_command_reads_the_local_errors_api()
    {
        var failure = await CreateReader().GetFailedMessageByIdAsync("failed-001");

        Assert.NotNull(failure);
        Assert.Equal("order-10042", failure.ConversationId);
        Assert.Equal("{\"OrderId\":\"order-10042\"}", failure.BodyPreview);
        Assert.Contains("/api/errors/last/failed-001", testApi.RequestedPaths);
        Assert.Contains("/api/errors/failed-001", testApi.RequestedPaths);
    }

    [Fact]
    public async Task Endpoint_health_command_reads_the_local_endpoints_api()
    {
        var endpoints = await CreateReader().GetEndpointHealthAsync();

        Assert.Equal(2, endpoints.Count);
        Assert.Contains(endpoints, endpoint => endpoint is { Endpoint: "Sales.Endpoint", Status: "Healthy", FailedMessages: 1 });
        Assert.Contains("/api/endpoints", testApi.RequestedPaths);
    }

    [Fact]
    public async Task Trace_command_reads_the_local_conversation_api()
    {
        var trace = await CreateReader().GetMessageTraceAsync("order-10042");

        Assert.NotNull(trace);
        Assert.Equal("order-10042", trace.ConversationId);
        Assert.Equal(2, trace.Activities.Count);
        Assert.Contains(trace.Activities, activity => activity.Intent == MessageIntent.Command);
        Assert.Contains(trace.Activities, activity => activity.Intent == MessageIntent.Event);
        Assert.Contains("/api/conversations/order-10042?per_page=100", testApi.RequestedPaths);
    }

    [Fact]
    public async Task Saga_command_reads_the_local_saga_history_api()
    {
        var saga = await CreateReader().GetSagaByIdAsync(LocalServiceControlApi.SagaId);

        Assert.NotNull(saga);
        Assert.Equal(SagaStatus.Completed, saga.Status);
        Assert.Equal("order-10042", saga.State!["OrderId"]);
        Assert.Equal(3, saga.Transitions.Count);
        Assert.Contains($"/api/sagas/{LocalServiceControlApi.SagaId}?per_page=100", testApi.RequestedPaths);
    }

    private ServiceControlHttpOperationsReader CreateReader()
    {
        var client = testApi.CreateClient();
        client.BaseAddress = LocalServiceControlApi.ApiBaseUri;
        var options = Options.Create(new ServiceControlOptions { ApiBaseUrl = client.BaseAddress.AbsoluteUri, PageSize = 100 });
        return new ServiceControlHttpOperationsReader(client, options);
    }
}

public sealed class LocalServiceControlApi : IAsyncLifetime
{
    public const string SagaId = "c21f3b2c-567c-4a12-b3aa-6eb8170ca5bb";
    public static readonly Uri ApiBaseUri = new("http://servicecontrol.test/api/", UriKind.Absolute);

    private TestServer? server;

    public List<string> RequestedPaths { get; } = [];

    public Task InitializeAsync()
    {
        server = new TestServer(new WebHostBuilder()
            .ConfigureServices(services => services.AddRouting())
            .Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    RequestedPaths.Add($"{context.Request.Path}{context.Request.QueryString}");
                    await next();
                });
                app.UseRouting();
                app.UseEndpoints(endpoints => MapRoutes(endpoints));
            }));

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        server?.Dispose();
        server = null;
        return Task.CompletedTask;
    }

    public HttpClient CreateClient()
        => server?.CreateClient() ?? throw new InvalidOperationException("The local ServiceControl API is not running.");

    private static void MapRoutes(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/errors", () => JsonFixture("errors.json"));
        endpoints.MapGet("/api/errors/last/failed-001", () => JsonFixture("error-last-failed-001.json"));
        endpoints.MapGet("/api/errors/failed-001", () => JsonFixture("error-details-failed-001.json"));
        endpoints.MapGet("/api/endpoints", () => JsonFixture("endpoints.json"));
        endpoints.MapGet("/api/conversations/order-10042", () => JsonFixture("conversation-order-10042.json"));
        endpoints.MapGet($"/api/sagas/{SagaId}", () => JsonFixture("saga-c21f3b2c.json"));
    }

    private static IResult JsonFixture(string name)
        => Results.Content(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)),
            "application/json");

}
