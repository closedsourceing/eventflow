using EventFlowMcp.Core;
using EventFlowMcp.Core.Tools;
using EventFlowMcp.Rag.Http;
using EventFlowMcp.ServiceControl.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddCommandLine(args);

builder.Logging.AddConsole(options =>
{
    // MCP stdio uses stdout for protocol messages, so logs must go to stderr.
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

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
    .WithStdioServerTransport()
    .WithTools<NServiceBusOperationsTools>()
    .WithTools<ArchitectureKnowledgeTools>()
    .WithTools<DistributedSystemsAdvisorTools>();

await builder.Build().RunAsync();
