namespace GraphData.Api.Models;

public sealed class GraphTraversalRequest
{
    public IReadOnlyCollection<IReadOnlyCollection<string>> Roots { get; init; } =
        Array.Empty<IReadOnlyCollection<string>>();

    public string Traversal { get; init; } = "type-closure";

    public int? MaxNodes { get; init; }

    public int? MaxEdges { get; init; }
}
