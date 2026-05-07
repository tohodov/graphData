namespace GraphData.McpTray;

internal sealed class McpTrayMessage
{
    public string MessageType { get; init; } = "log";

    public string InstanceId { get; init; } = string.Empty;

    public int ProcessId { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset StartedAt { get; init; }

    public string BaseDirectory { get; init; } = string.Empty;

    public string? GraphStorageRoot { get; init; }

    public bool DebuggerAttached { get; init; }

    public string? State { get; init; }

    public int? Level { get; init; }

    public string? Category { get; init; }

    public int? EventId { get; init; }

    public string? Text { get; init; }

    public string? Exception { get; init; }
}
