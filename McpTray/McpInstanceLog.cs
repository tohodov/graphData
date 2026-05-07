using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text;

namespace GraphData.McpTray;

internal sealed class McpInstanceLog
{
    private readonly List<McpLogEntry> _entries = [];
    private static readonly Regex SessionPrefixRegex = new(
        @"^Server\s+(?<server>(?:\([^)]*\)\s*)+)(?:,\s*Client\s+(?<client>(?:\([^)]*\)\s*)+))?\s+(?<message>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SessionValueRegex = new(
        @"\((?<value>[^)]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public required string InstanceId { get; init; }

    public int ProcessId { get; private set; }

    public string State { get; private set; } = "unknown";

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public string BaseDirectory { get; private set; } = string.Empty;

    public string? GraphStorageRoot { get; private set; }

    public bool DebuggerAttached { get; private set; }

    public string? Server { get; private set; }

    public string? Client { get; private set; }

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

        var text = ExtractSessionFields(message.Text);

        _entries.Add(new McpLogEntry
        {
            Timestamp = message.Timestamp,
            MessageType = message.MessageType,
            Level = FormatLevel(message.Level),
            Category = message.Category,
            EventId = message.EventId,
            Text = text,
            Exception = message.Exception
        });
        LogVersion++;

        if (_entries.Count > 2000)
        {
            _entries.RemoveRange(0, _entries.Count - 2000);
        }
    }

    private string? ExtractSessionFields(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var match = SessionPrefixRegex.Match(text);
        if (!match.Success)
        {
            return text;
        }

        UpdateSessionField(match.Groups["server"].Value, value => Server = value);
        UpdateSessionField(match.Groups["client"].Value, value => Client = value);

        var message = match.Groups["message"].Value.TrimStart();
        return string.IsNullOrWhiteSpace(message) ? text : message;
    }

    private static void UpdateSessionField(string value, Action<string> update)
    {
        var normalized = NormalizeSessionValue(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            update(normalized);
        }
    }

    private static string NormalizeSessionValue(string value)
    {
        return string.Join(" / ", SessionValueRegex.Matches(value)
            .Select(static match => match.Groups["value"].Value.Trim())
            .Where(static part => !string.IsNullOrWhiteSpace(part)));
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
