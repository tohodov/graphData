namespace GraphData.McpTray;

internal sealed class McpLogCollector
{
    private readonly Dictionary<string, McpInstanceLog> _instances = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<McpLogCollectorChangedEventArgs>? Changed;

    public IReadOnlyList<McpInstanceLog> Instances => _instances.Values
        .OrderBy(static instance => instance.StartedAt == default ? DateTimeOffset.MaxValue : instance.StartedAt)
        .ThenBy(static instance => instance.ProcessId)
        .ThenBy(static instance => instance.InstanceId, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public int RunningCount => _instances.Values.Count(static instance => instance.IsRunning);

    public void Apply(McpTrayMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.InstanceId))
        {
            return;
        }

        if (!_instances.TryGetValue(message.InstanceId, out var instance))
        {
            instance = new McpInstanceLog { InstanceId = message.InstanceId };
            _instances.Add(message.InstanceId, instance);
        }

        instance.Apply(message);
        Changed?.Invoke(this, new McpLogCollectorChangedEventArgs(message.InstanceId));
    }

    public McpInstanceLog? Get(string instanceId)
    {
        return _instances.GetValueOrDefault(instanceId);
    }
}

internal sealed class McpLogCollectorChangedEventArgs(string instanceId) : EventArgs
{
    public string InstanceId { get; } = instanceId;
}
