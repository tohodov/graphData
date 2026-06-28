namespace GraphData.Api.Models;

public sealed class SubgraphRequest {
    public required IReadOnlyCollection<IReadOnlyCollection<string>> Paths { get; init; }
    public required int MaxDepth { get; init; }
    public bool IncludeDisconnectedRoots { get; init; }
    public bool IgnoreMissingRoots { get; init; }
}
