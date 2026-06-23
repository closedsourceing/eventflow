using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using EventFlowMcp.Abstractions.Operations;
using EventFlowMcp.Core;
using EventFlowMcp.Core.Tools;
using EventFlowMcp.LocalProjection;
using EventFlowMcp.PostgresProjection;
using EventFlowMcp.Rag.Http;
using EventFlowMcp.ServiceControl.Http;
using EventFlowMcp.Server;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
var maxRequestBodyBytes = Math.Clamp(
    builder.Configuration.GetValue<long?>("Mcp:MaxRequestBodyBytes") ?? 262_144,
    4_096,
    1_048_576);
var requestsPerMinute = Math.Clamp(
    builder.Configuration.GetValue<int?>("Mcp:RateLimitPerMinute") ?? 120,
    1,
    10_000);

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBodyBytes);
builder.Services.AddHealthChecks()
    .AddCheck<OperationsReaderHealthCheck>("operations-reader", tags: ["ready"]);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("mcp", limiterOptions =>
    {
        limiterOptions.PermitLimit = requestsPerMinute;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
        limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});

builder.Services.AddEventFlowMcpCore(builder.Configuration);

if (string.Equals(builder.Configuration["Operations:Provider"], "ServiceControl", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddServiceControlOperationsReader(builder.Configuration);
}

if (string.Equals(builder.Configuration["Operations:Provider"], "LocalProjection", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddLocalProjectionOperationsReader(builder.Configuration);
}

if (string.Equals(builder.Configuration["Operations:Provider"], "PostgresProjection", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddPostgresProjectionOperationsReader(builder.Configuration);
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
var mcpRequests = new Meter("EventFlowMcp.Server", "0.2.0")
    .CreateCounter<long>("eventflow.mcp.requests", unit: "requests", description: "MCP HTTP requests handled by EventFlow.");
app.Lifetime.ApplicationStopped.Register(() => mcpRequests.Meter.Dispose());

var configuredApiKey = app.Configuration["Mcp:ApiKey"];
if (string.IsNullOrWhiteSpace(configuredApiKey))
{
    if (app.Environment.IsProduction())
        throw new InvalidOperationException("Mcp:ApiKey must be configured when ASPNETCORE_ENVIRONMENT is Production.");

    app.Logger.LogWarning("EventFlow MCP is running without an API key. This is suitable only for local development.");
}
else if (configuredApiKey.Length < 32)
{
    app.Logger.LogWarning("Mcp:ApiKey is shorter than 32 characters. Use a high-entropy secret for shared deployments.");
}

app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    await next();
});

app.UseRateLimiter();

app.Use(async (context, next) =>
{
    var isMcpRequest = context.Request.Path.StartsWithSegments("/mcp");
    var startedAt = Stopwatch.GetTimestamp();
    var expectedApiKey = configuredApiKey;

    try
    {
        if (!string.IsNullOrWhiteSpace(expectedApiKey) && isMcpRequest)
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
    }
    finally
    {
        if (isMcpRequest)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            mcpRequests.Add(1,
                new KeyValuePair<string, object?>("http.response.status_code", context.Response.StatusCode));
            app.Logger.LogInformation(
                "MCP request completed with status {StatusCode} in {ElapsedMs:0} ms. RequestId: {RequestId}",
                context.Response.StatusCode,
                elapsedMs,
                context.TraceIdentifier);
        }
    }
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "EventFlowMcp.Server" }));
app.MapHealthChecks("/readyz", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
app.MapMcp("/mcp").RequireRateLimiting("mcp");

app.Run();

static bool ApiKeysMatch(string expected, string provided)
{
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    var providedBytes = Encoding.UTF8.GetBytes(provided);

    return expectedBytes.Length == providedBytes.Length
           && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
}
