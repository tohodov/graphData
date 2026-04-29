using GraphData.Core.Extensions;
using GraphData.Mcp.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SymLinkStorage;

var builder = Host.CreateApplicationBuilder(args);

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
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
