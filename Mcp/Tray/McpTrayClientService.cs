using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GraphData.Mcp.Tray;

internal sealed class McpTrayClientService(
    IConfiguration configuration,
    McpTrayLogSink sink,
    ILogger<McpTrayClientService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IConfiguration _configuration = configuration;
    private readonly McpTrayLogSink _sink = sink;
    private readonly ILogger<McpTrayClientService> _logger = logger;
    private readonly string _instanceId = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ExecuteCoreAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MCP tray client stopped unexpectedly.");
        }
    }

    private async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows() || !IsTrayEnabled())
        {
            return;
        }

        var pipeName = GetPipeName();
        StartTrayApp(pipeName);

        await using var pipe = await ConnectAsync(pipeName, stoppingToken);
        if (pipe is null)
        {
            _logger.LogDebug("MCP tray pipe {PipeName} was not available.", pipeName);
            return;
        }

        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false))
        {
            AutoFlush = true
        };

        await WriteAsync(writer, CreateLifecycleMessage("instance_started", "running", "MCP instance connected."), CancellationToken.None);

        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(2));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var logAvailable = _sink.Reader.WaitToReadAsync(stoppingToken).AsTask();
                var heartbeatTick = heartbeat.WaitForNextTickAsync(stoppingToken).AsTask();
                var completed = await Task.WhenAny(logAvailable, heartbeatTick);

                if (completed == heartbeatTick && await heartbeatTick)
                {
                    await WriteAsync(writer, CreateLifecycleMessage("heartbeat", "running", null), stoppingToken);
                }

                if (completed == logAvailable && await logAvailable)
                {
                    while (_sink.Reader.TryRead(out var logMessage))
                    {
                        await WriteAsync(writer, WithInstance(logMessage), stoppingToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "MCP tray pipe disconnected.");
        }
        finally
        {
            try
            {
                await WriteAsync(writer, CreateLifecycleMessage("instance_stopped", "stopped", "MCP instance stopped."), CancellationToken.None);
            }
            catch
            {
            }
        }
    }

    private bool IsTrayEnabled()
    {
        return !string.Equals(_configuration["McpTray:Enabled"], "false", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Environment.GetEnvironmentVariable("GRAPHDATA_MCP_TRAY"), "0", StringComparison.Ordinal);
    }

    private string GetPipeName()
    {
        return string.IsNullOrWhiteSpace(_configuration["McpTray:PipeName"])
            ? "GraphDataMcpTray"
            : _configuration["McpTray:PipeName"]!;
    }

    private async Task<NamedPipeClientStream?> ConnectAsync(string pipeName, CancellationToken stoppingToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (!stoppingToken.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.ConnectAsync(750, stoppingToken);
                return pipe;
            }
            catch (TimeoutException)
            {
                await pipe.DisposeAsync();
                await Task.Delay(250, stoppingToken);
            }
            catch
            {
                await pipe.DisposeAsync();
                await Task.Delay(250, stoppingToken);
            }
        }

        return null;
    }

    private McpTrayMessage WithInstance(McpTrayMessage message)
    {
        return new McpTrayMessage
        {
            MessageType = message.MessageType,
            InstanceId = _instanceId,
            ProcessId = Environment.ProcessId,
            Timestamp = message.Timestamp,
            StartedAt = _startedAt,
            BaseDirectory = AppContext.BaseDirectory,
            GraphStorageRoot = _configuration["GraphStorage:RootPath"],
            DebuggerAttached = Debugger.IsAttached,
            State = message.State,
            Level = message.Level,
            Category = message.Category,
            EventId = message.EventId,
            Text = message.Text,
            Exception = message.Exception
        };
    }

    private McpTrayMessage CreateLifecycleMessage(string messageType, string state, string? text)
    {
        return new McpTrayMessage
        {
            MessageType = messageType,
            InstanceId = _instanceId,
            ProcessId = Environment.ProcessId,
            Timestamp = DateTimeOffset.UtcNow,
            StartedAt = _startedAt,
            BaseDirectory = AppContext.BaseDirectory,
            GraphStorageRoot = _configuration["GraphStorage:RootPath"],
            DebuggerAttached = Debugger.IsAttached,
            State = state,
            Text = text
        };
    }

    private static async Task WriteAsync(StreamWriter writer, McpTrayMessage message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
    }

    private void StartTrayApp(string pipeName)
    {
        var trayPath = Path.Combine(AppContext.BaseDirectory, "McpTray.exe");
        if (!File.Exists(trayPath))
        {
            _logger.LogDebug("MCP tray app was not found at {TrayPath}.", trayPath);
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                StartTrayAppWindows(trayPath, pipeName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to start MCP tray app.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void StartTrayAppWindows(string trayPath, string pipeName)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = trayPath,
            Arguments = $"--pipe-name \"{pipeName.Replace("\"", "\\\"")}\"",
            WorkingDirectory = Path.GetDirectoryName(trayPath) ?? AppContext.BaseDirectory,
            UseShellExecute = true
        });
    }
}
