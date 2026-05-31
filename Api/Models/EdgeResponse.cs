namespace GraphData.Api.Models;

public sealed record EdgeResponse
{
    public string? NeighborLocalId { get; init; }

    public required string SourceLocalId { get; init; }

    public required string SourceGlobalId { get; init; }

    public required string TargetLocalId { get; init; }

    public required string TargetGlobalId { get; init; }
}
