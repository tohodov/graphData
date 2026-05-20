namespace GraphData.Core.Models;

public sealed record NodeSearchQuery
{
    public string? Text { get; init; }

    public HierarchySearchPattern? DescendantOf { get; init; }

    public ConnectionSearchPattern[] ConnectedToAll { get; init; } = [];

    public ConnectionSearchPattern[] ConnectedToAny { get; init; } = [];

    public int Limit { get; init; } = 50;
}

public sealed record HierarchySearchPattern
{
    public required string NodeName { get; init; }

    public int MaxDepth { get; init; } = 8;
}

public sealed record ConnectionSearchPattern
{
    public required string NodeName { get; init; }

    public int MaxDepth { get; init; } = 2;

    public bool IncludeSelf { get; init; }
}

public sealed record NodeSearchMatch
{
    public required Node Node { get; init; }

    public double Score { get; init; }

    public IReadOnlyCollection<string> MatchedBy { get; init; } = Array.Empty<string>();
}
