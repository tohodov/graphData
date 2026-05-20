namespace GraphData.Api.Models;

public sealed class GraphViewerNodeResponse
{
    public required NodeResponse Node { get; init; }

    public IReadOnlyCollection<GraphViewerEdgeResponse> Edges { get; init; } =
        Array.Empty<GraphViewerEdgeResponse>();
}

public sealed class GraphViewerEdgeResponse
{
    public required string SourceName { get; init; }

    public required string TargetName { get; init; }

    public required NodeResponse TargetNode { get; init; }
}
