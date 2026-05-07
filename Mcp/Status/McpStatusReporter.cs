using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GraphData.Mcp.Status;

internal sealed class McpStatusReporter(
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<McpStatusReporter> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly IConfiguration _configuration = configuration;
    private readonly IHostApplicationLifetime _lifetime = lifetime;
    private readonly ILogger<McpStatusReporter> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var statusDirectory = GetStatusDirectory();
        Directory.CreateDirectory(statusDirectory);

        var statusPath = Path.Combine(statusDirectory, $"mcp-{Environment.ProcessId}.json");

        WriteStatus(statusPath, statusDirectory, "starting", "MCP server is starting.");
        StartTrayApp(statusDirectory);
        WriteStatus(statusPath, statusDirectory, "running", "MCP server is running.");

        using var stoppingRegistration = _lifetime.ApplicationStopping.Register(() =>
        {
            WriteStatus(statusPath, statusDirectory, "stopping", "MCP server is stopping.");
        });

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                WriteStatus(statusPath, statusDirectory, "running", "MCP server is running.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            WriteStatus(statusPath, statusDirectory, "stopped", "MCP server stopped.");
        }
    }

    private string GetStatusDirectory()
    {
        var configured = _configuration["McpStatus:StatusDirectory"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.Combine(AppContext.BaseDirectory, "status");
    }

    private void WriteStatus(string statusPath, string statusDirectory, string state, string message)
    {
        try
        {
            var status = new McpServerStatus
            {
                ProcessId = Environment.ProcessId,
                State = state,
                StartedAt = _startedAt,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
                BaseDirectory = AppContext.BaseDirectory,
                StatusDirectory = statusDirectory,
                GraphStorageRoot = _configuration["GraphStorage:RootPath"],
                DebuggerAttached = Debugger.IsAttached,
                Message = message
            };

            File.WriteAllText(statusPath, JsonSerializer.Serialize(status, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write MCP status.");
        }
    }

    private void StartTrayApp(string statusDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (string.Equals(_configuration["McpStatus:TrayEnabled"], "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("GRAPHDATA_MCP_TRAY"), "0", StringComparison.Ordinal))
        {
            return;
        }

        var trayPath = Path.Combine(AppContext.BaseDirectory, "McpTray.exe");
        if (!File.Exists(trayPath))
        {
            _logger.LogDebug("MCP tray app was not found at {TrayPath}.", trayPath);
            return;
        }

        try
        {
            StartTrayAppWindows(trayPath, statusDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to start MCP tray app.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void StartTrayAppWindows(string trayPath, string statusDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = trayPath,
            Arguments = $"--status-dir \"{statusDirectory.Replace("\"", "\\\"")}\"",
            WorkingDirectory = Path.GetDirectoryName(trayPath) ?? AppContext.BaseDirectory,
            UseShellExecute = true
        });
    }
}
