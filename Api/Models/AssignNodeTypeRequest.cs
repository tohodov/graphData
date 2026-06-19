namespace GraphData.Api.Models;

public sealed class AssignNodeTypeRequest
{
    public required string[] NodeGlobalId { get; init; }

    public required string[] TypeGlobalId { get; init; }
}
