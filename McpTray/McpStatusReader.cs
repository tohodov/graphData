using System.Text.Json;

namespace GraphData.McpTray;

internal sealed class McpStatusReader(string statusDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _statusDirectory = statusDirectory;

    public McpStatusSnapshot Read()
    {
        var servers = new List<McpServerStatus>();

        if (Directory.Exists(_statusDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(_statusDirectory, "mcp-*.json"))
            {
                var status = TryRead(path);
                if (status is not null)
                {
                    servers.Add(status);
                }
            }
        }

        return new McpStatusSnapshot
        {
            StatusDirectory = _statusDirectory,
            Servers = servers
                .OrderByDescending(static server => server.LastHeartbeatAt)
                .ToArray()
        };
    }

    private static McpServerStatus? TryRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<McpServerStatus>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
