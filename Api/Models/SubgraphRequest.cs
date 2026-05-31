namespace GraphData.Api.Models;

public sealed class SubgraphRequest {
    public required IReadOnlyCollection<IReadOnlyCollection<string>> GlobalIds { get; init; }
    public required int MaxDepth { get; init; }
}
