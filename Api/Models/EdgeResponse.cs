namespace GraphData.Api.Models;

public sealed record EdgeResponse
{
    public string? Kind { get; init; }

    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();

    public string? NeighborLocalId { get; init; }

    public required string Node1LocalId { get; init; }

    public required string Node1InternalId { get; init; }

    public required string Node2LocalId { get; init; }

    public required string Node2InternalId { get; init; }
}
