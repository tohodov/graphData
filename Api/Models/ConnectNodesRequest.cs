namespace GraphData.Api.Models;

public sealed class ConnectNodesRequest
{
    public required string SourceName { get; init; }

    public required string TargetName { get; init; }
}
