namespace GraphData.Api.Models;

public sealed class ChangeEdgeTypeRequest
{
    public required string[] RelationGlobalId { get; init; }

    public required string[] TypeGlobalId { get; init; }
}
