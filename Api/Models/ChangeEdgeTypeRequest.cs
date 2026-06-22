namespace GraphData.Api.Models;

public sealed class ChangeEdgeTypeRequest
{
    public string[]? RelationGlobalId { get; init; }

    public string[]? Node1InternalId { get; init; }

    public string[]? Node2InternalId { get; init; }

    public required string[] TypeGlobalId { get; init; }

    public string[]? RelationParentGlobalId { get; init; }

    public string? RelationLocalId { get; init; }
}
