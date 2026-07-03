namespace GraphData.Api.Models;

public sealed class GraphObservationResponse
{
    public required string Traversal { get; init; }

    public bool Exhaustive { get; init; }

    public ICollection<NodeResponse> Nodes { get; init; } = Array.Empty<NodeResponse>();

    public ICollection<EdgeResponse> Edges { get; init; } = Array.Empty<EdgeResponse>();

    public ICollection<GraphObservationBoundaryResponse> Boundary { get; init; } =
        Array.Empty<GraphObservationBoundaryResponse>();
}

public sealed record GraphObservationBoundaryResponse
{
    public required string InternalId { get; init; }

    public required string Reason { get; init; }
}
