using System.Security.Cryptography;
using System.Text;
using EventFlowMcp.Core;
using EventFlowMcp.Core.Tools;
using EventFlowMcp.Rag.Http;
using EventFlowMcp.ServiceControl.Http;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEventFlowMcpCore(builder.Configuration);

if (string.Equals(builder.Configuration["Operations:Provider"], "ServiceControl", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddServiceControlOperationsReader(builder.Configuration);
}

if (string.Equals(builder.Configuration["Rag:Provider"], "Http", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpRagRetriever(builder.Configuration);
}

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        // Stateless mode is easier to scale behind Kubernetes/Container Apps.
        options.Stateless = true;
    })
    .WithTools<NServiceBusOperationsTools>()
    .WithTools<ArchitectureKnowledgeTools>()
    .WithTools<DistributedSystemsAdvisorTools>();

var app = builder.Build();

if (string.IsNullOrWhiteSpace(app.Configuration["Mcp:ApiKey"]))
{
    app.Logger.LogWarning("EventFlow MCP is running without an API key. This is suitable only for local development.");
}

app.Use(async (context, next) =>
{
    var expectedApiKey = app.Configuration["Mcp:ApiKey"];

    if (!string.IsNullOrWhiteSpace(expectedApiKey) && context.Request.Path.StartsWithSegments("/mcp"))
    {
        var providedApiKey = context.Request.Headers["X-MCP-API-Key"].ToString();
        if (string.IsNullOrWhiteSpace(providedApiKey)
            && context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            providedApiKey = context.Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        }

        if (!ApiKeysMatch(expectedApiKey, providedApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsync("Unauthorized");
            return;
        }
    }

    await next();
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "EventFlowMcp.Server" }));
app.MapMcp("/mcp");

app.Run();

static bool ApiKeysMatch(string expected, string provided)
{
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    var providedBytes = Encoding.UTF8.GetBytes(provided);

    return expectedBytes.Length == providedBytes.Length
           && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
}
