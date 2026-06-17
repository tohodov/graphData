using System.Diagnostics;
using Client;
using GraphData.Api.Runtime;
using GraphData.Core.Extensions;
using GraphData.Mcp.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Storage;

var builder = Host.CreateApplicationBuilder(args);

if (args.Any(static arg =>
        string.Equals(arg, "--debug", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "--debug-wait", StringComparison.OrdinalIgnoreCase)))
    while (!Debugger.IsAttached) {
        Console.Error.WriteLine("graphData MCP is waiting for a debugger to attach...");
        await Task.Delay(250);
    }

if (!builder.Configuration.GetSection("GraphStorage").Exists())
    builder.Configuration.AddJsonFile(
        Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
        optional: true,
        reloadOnChange: false);

builder.Logging.AddConsole(options => {
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.AddTracker();

builder.Services.AddSingleton<ICancellationTokenAccessor, McpCancellationTokenAccessor>();
builder.Services.AddDomain();
builder.Services.AddSymLinkStorage(builder.Configuration.GetSection("GraphStorage"));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithMcpTrackerMessageFilters()
    .WithListResourcesHandler((_, _) => ValueTask.FromResult(new ListResourcesResult { Resources = [] }))
    .WithListResourceTemplatesHandler((_, _) => ValueTask.FromResult(new ListResourceTemplatesResult { ResourceTemplates = [] }))
    .WithToolsFromAssembly(serializerOptions: GraphJsonSerializerOptions.Create());

await builder.Build().RunAsync();
