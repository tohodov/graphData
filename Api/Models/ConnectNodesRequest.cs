namespace GraphData.Api.Models;

public sealed class ConnectNodesRequest
{
    public required string[] SourceGlobalId { get; init; }

    public required string[] TargetGlobalId { get; init; }
}
