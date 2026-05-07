using GraphData.Core.Extensions;
using GraphData.Mcp.Runtime;
using GraphData.Mcp.Status;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SymLinkStorage;
using System.Diagnostics;

var builder = Host.CreateApplicationBuilder(args);

if (ShouldWaitForDebugger(args))
{
    Console.Error.WriteLine("graphData MCP is waiting for a debugger to attach...");
    while (!Debugger.IsAttached)
    {
        await Task.Delay(250);
    }
}

if (!builder.Configuration.GetSection("GraphStorage").Exists())
{
    builder.Configuration.AddJsonFile(
        Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
        optional: true,
        reloadOnChange: false);
}

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<ICancellationTokenAccessor, McpCancellationTokenAccessor>();
builder.Services.AddGraphCore();
builder.Services.AddSymLinkStorage(builder.Configuration.GetSection("GraphStorage"));
builder.Services.AddHostedService<McpStatusReporter>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

static bool ShouldWaitForDebugger(string[] args)
{
    return args.Any(static arg => string.Equals(arg, "--debug-wait", StringComparison.OrdinalIgnoreCase))
        || string.Equals(Environment.GetEnvironmentVariable("GRAPHDATA_MCP_DEBUG_WAIT"), "1", StringComparison.Ordinal);
}
