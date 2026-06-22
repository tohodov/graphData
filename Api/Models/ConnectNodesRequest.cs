namespace GraphData.Api.Models;

public sealed class ConnectNodesRequest
{
    public required string[] Node1InternalId { get; init; }

    public required string[] Node2InternalId { get; init; }
}
