using System.Diagnostics;
using System.Text;

namespace GraphData.McpTray;

internal sealed class McpInstanceLog
{
    private readonly List<McpLogEntry> _entries = [];

    public required string InstanceId { get; init; }

    public int ProcessId { get; private set; }

    public string State { get; private set; } = "unknown";

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public string BaseDirectory { get; private set; } = string.Empty;

    public string? GraphStorageRoot { get; private set; }

    public bool DebuggerAttached { get; private set; }

    public IReadOnlyList<McpLogEntry> Entries => _entries;

    public long LogVersion { get; private set; }

    public bool IsRunning
    {
        get
        {
            if (!string.Equals(State, "running", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - LastSeenAt > TimeSpan.FromSeconds(8))
            {
                return false;
            }

            try
            {
                using var process = Process.GetProcessById(ProcessId);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    public string DisplayName
    {
        get
        {
            var marker = IsRunning ? "running" : "stale";
            var started = StartedAt == default ? "-" : StartedAt.ToLocalTime().ToString("HH:mm:ss");
            return $"PID {ProcessId} | {marker} | started {started}";
        }
    }

    public void Apply(McpTrayMessage message)
    {
        ProcessId = message.ProcessId;
        StartedAt = message.StartedAt;
        LastSeenAt = message.Timestamp;
        BaseDirectory = message.BaseDirectory;
        GraphStorageRoot = message.GraphStorageRoot;
        DebuggerAttached = message.DebuggerAttached;

        if (!string.IsNullOrWhiteSpace(message.State))
        {
            State = message.State;
        }

        if (string.Equals(message.MessageType, "heartbeat", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _entries.Add(new McpLogEntry
        {
            Timestamp = message.Timestamp,
            MessageType = message.MessageType,
            Level = FormatLevel(message.Level),
            Category = message.Category,
            EventId = message.EventId,
            Text = message.Text,
            Exception = message.Exception
        });
        LogVersion++;

        if (_entries.Count > 2000)
        {
            _entries.RemoveRange(0, _entries.Count - 2000);
        }
    }

    public string FormatLogText()
    {
        var builder = new StringBuilder();

        foreach (var entry in _entries)
        {
            builder.Append(entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"));
            builder.Append(" [");
            builder.Append(entry.Level ?? entry.MessageType);
            builder.Append("] ");

            if (!string.IsNullOrWhiteSpace(entry.Category))
            {
                builder.Append(entry.Category);
                if (entry.EventId is not null)
                {
                    builder.Append('#');
                    builder.Append(entry.EventId);
                }

                builder.Append(": ");
            }

            builder.AppendLine(entry.Text ?? entry.MessageType);

            if (!string.IsNullOrWhiteSpace(entry.Exception))
            {
                builder.AppendLine(entry.Exception);
            }
        }

        return builder.ToString();
    }

    private static string? FormatLevel(int? level)
    {
        return level switch
        {
            0 => "Trace",
            1 => "Debug",
            2 => "Information",
            3 => "Warning",
            4 => "Error",
            5 => "Critical",
            6 => "None",
            _ => null
        };
    }
}

internal sealed class McpLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }

    public required string MessageType { get; init; }

    public string? Level { get; init; }

    public string? Category { get; init; }

    public int? EventId { get; init; }

    public string? Text { get; init; }

    public string? Exception { get; init; }
}
