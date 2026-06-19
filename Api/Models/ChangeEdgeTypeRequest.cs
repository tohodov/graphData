namespace GraphData.Api.Models;

public sealed class ChangeEdgeTypeRequest
{
    public string[]? RelationGlobalId { get; init; }

    public string[]? SourceGlobalId { get; init; }

    public string[]? TargetGlobalId { get; init; }

    public required string[] TypeGlobalId { get; init; }

    public string[]? RelationRootGlobalId { get; init; }

    public string? RelationLocalId { get; init; }
}
