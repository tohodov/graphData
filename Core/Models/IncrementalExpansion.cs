namespace GraphData.Core.Models;

public sealed record IncrementalExpansionRequest
{
    public required string[] RootContextNodes { get; init; }
    public required ExpansionNodeUpsert[] Upserts { get; init; }
    public required ExpansionConnection[] Connections { get; init; }
    public int ContextDepth { get; init; } = 2;
}

public sealed record ExpansionNodeUpsert
{
    public required string Name { get; init; }
    public Dictionary<string, string>? Attributes { get; init; }
    public bool MergeAttributes { get; init; } = true;
}

public sealed record ExpansionConnection
{
    public required string SourceName { get; init; }
    public required string TargetName { get; init; }
}

public sealed record IncrementalExpansionResult
{
    public required IReadOnlyCollection<string> CreatedNodes { get; init; }
    public required IReadOnlyCollection<string> UpdatedNodes { get; init; }
    public required IReadOnlyCollection<string> ConnectedPairs { get; init; }
    public required Subgraph ContextSubgraph { get; init; }
}
