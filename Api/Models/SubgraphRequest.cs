namespace GraphData.Api.Models;

public sealed class SubgraphRequest {
    public required IReadOnlyCollection<IReadOnlyCollection<string>> Nodes { get; init; }
    public required int MaxDepth { get; init; }
}
