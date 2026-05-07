namespace GraphData.McpTray;

internal sealed class McpStatusSnapshot
{
    public required string StatusDirectory { get; init; }

    public required IReadOnlyList<McpServerStatus> Servers { get; init; }

    public McpServerStatus? CurrentServer => Servers
        .Where(IsRunning)
        .OrderByDescending(static server => server.LastHeartbeatAt)
        .FirstOrDefault()
        ?? Servers
            .OrderByDescending(static server => server.LastHeartbeatAt)
            .FirstOrDefault();

    public bool HasRunningServer => Servers.Any(static server => IsRunning(server));

    public string DisplayState
    {
        get
        {
            var current = CurrentServer;
            if (current is null)
            {
                return "No server status yet";
            }

            return IsRunning(current)
                ? $"Running (PID {current.ProcessId})"
                : $"Stale or stopped ({current.State}, PID {current.ProcessId})";
        }
    }

    public static bool IsRunning(McpServerStatus status)
    {
        if (!string.Equals(status.State, "running", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status.State, "starting", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - status.LastHeartbeatAt > TimeSpan.FromSeconds(8))
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(status.ProcessId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
