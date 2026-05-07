namespace GraphData.Mcp.Status;

internal sealed class McpServerStatus
{
    public required int ProcessId { get; init; }

    public required string State { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset LastHeartbeatAt { get; init; }

    public required string BaseDirectory { get; init; }

    public required string StatusDirectory { get; init; }

    public string? GraphStorageRoot { get; init; }

    public bool DebuggerAttached { get; init; }

    public string? Message { get; init; }
}
