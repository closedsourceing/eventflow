using NServiceBus;
using NServiceBusDemo.Api;
using EventFlowMcp.Abstractions.Operations;
using EventFlowMcp.LocalProjection;
using EventFlowMcp.NServiceBus;
using EventFlowMcp.PostgresProjection;

var builder = WebApplication.CreateBuilder(args);
var learningTransportDirectory = Path.Combine(Path.GetTempPath(), $"eventflow-nservicebus-demo-{Environment.ProcessId}");
var projectionProvider = builder.Configuration["EventFlow:Projection:Provider"] ?? "LocalJson";
IOperationsProjectionWriter projectionWriter;
string projectionDescription;

if (string.Equals(projectionProvider, "LocalJson", StringComparison.OrdinalIgnoreCase))
{
    var projectionPath = builder.Configuration["EventFlow:ProjectionPath"]
        ?? Path.Combine(builder.Environment.ContentRootPath, ".eventflow", "nservicebus-demo.operations.json");
    var projection = new LocalOperationsProjection(projectionPath);
    projection.Reset();
    projectionWriter = projection;
    projectionDescription = projection.FilePath;
}
else if (string.Equals(projectionProvider, "Postgres", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration["EventFlow:Projection:Postgres:ConnectionString"];
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("EventFlow:Projection:Postgres:ConnectionString is required for the Postgres demo projection.");

    var projection = new PostgresOperationsProjection(connectionString);
    await projection.InitializeAsync();
    await projection.ApplyRetentionAsync(builder.Configuration.GetValue("EventFlow:Projection:RetentionDays", 30));
    projectionWriter = projection;
    projectionDescription = "PostgreSQL EventFlow projection";
}
else
{
    throw new InvalidOperationException("EventFlow:Projection:Provider must be LocalJson or Postgres.");
}

Console.Error.WriteLine($"[demo] Configuring NServiceBus Learning Transport in {learningTransportDirectory}");
Console.Error.WriteLine($"[demo] Writing EventFlow operations projection to {projectionDescription}");

builder.Services.AddSingleton<DemoOrderStore>();
builder.Services.AddSingleton(projectionWriter);

var endpointConfiguration = new EndpointConfiguration("EventFlow.NServiceBusDemo.Api");
var transport = endpointConfiguration.UseTransport<LearningTransport>();
transport.StorageDirectory(learningTransportDirectory);
endpointConfiguration.UsePersistence<LearningPersistence>();
endpointConfiguration.UseSerialization<SystemJsonSerializer>();
endpointConfiguration.SendFailedMessagesTo("error");
endpointConfiguration.EnableInstallers(); // Required only for this self-contained local demo.
endpointConfiguration.Pipeline.Register(
    new EventFlowProjectionBehavior(
        projectionWriter,
        "EventFlow.NServiceBusDemo.Api",
        new OrderMessageCorrelationResolver()),
    "Records local NServiceBus handler and saga activity for EventFlow MCP.");

// The endpoint shares the Web API's dependency injection container. It starts and
// stops together with the application process.
builder.Host.UseNServiceBus(_ => endpointConfiguration);

var app = builder.Build();
var orders = app.Services.GetRequiredService<DemoOrderStore>();
Console.Error.WriteLine("[demo] Host built. Starting the NServiceBus endpoint and Web API.");

app.Lifetime.ApplicationStopped.Register(() => TryDeleteDirectory(learningTransportDirectory));

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "NServiceBusDemo.Api" }));

app.MapGet("/orders", () => Results.Ok(orders.GetAll()));

app.MapGet("/orders/{orderId}", (string orderId) =>
    orders.TryGet(orderId, out var order)
        ? Results.Ok(order)
        : Results.NotFound(new { message = $"Order '{orderId}' was not found." }));

app.MapPost("/orders/{orderId}/submit", async (string orderId, IMessageSession messages) =>
{
    if (!orders.TryMarkSubmissionRequested(orderId, out var order))
        return Results.NotFound(new { message = $"Order '{orderId}' was not found." });

    await messages.SendLocal(new SubmitOrder(order.Id, order.Customer, order.Total));
    return Results.Accepted($"/orders/{order.Id}", new { order.Id, order.Status, message = "SubmitOrder sent locally." });
});

app.MapPost("/orders/{orderId}/approve", async (string orderId, IMessageSession messages) =>
{
    if (!orders.TryGet(orderId, out var order))
        return Results.NotFound(new { message = $"Order '{orderId}' was not found." });

    await messages.SendLocal(new ApproveOrder(order.Id));
    return Results.Accepted($"/orders/{order.Id}", new { order.Id, message = "ApproveOrder sent locally." });
});

app.MapGet("/demo", () => Results.Ok(new
{
    orderId = DemoOrderStore.DemoOrderId,
    instructions = new[]
    {
        $"POST /orders/{DemoOrderStore.DemoOrderId}/submit",
        $"POST /orders/{DemoOrderStore.DemoOrderId}/approve",
        $"GET /orders/{DemoOrderStore.DemoOrderId}"
    },
    projection = projectionDescription,
    note = "This API uses NServiceBus Learning Transport. Configure EventFlow with the matching LocalProjection or PostgresProjection reader to inspect the real local handler and saga activity."
}));

await app.RunAsync();

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
    catch (IOException)
    {
        // The learning transport is a local demo aid. A later process can clean a
        // transient directory if the operating system still holds a file handle.
    }
}

public partial class Program;
